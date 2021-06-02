using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace DocTranslation
{
    public static class Translate
    {
        private static readonly string s_googleProjectId;
        private static readonly ICredential s_googleCredential;
        private static readonly FormOptions s_formOptions;
        private static readonly HttpClient s_httpClient;
        private static readonly JsonSerializerOptions s_jsonOptions;

        static Translate()
        {
            s_googleProjectId = Environment.GetEnvironmentVariable("GOOGLE_PROJECT_ID") ?? throw new InvalidOperationException("GOOGLE_PROJECT_ID is not set.");
            s_googleCredential = GoogleCredential.FromJson(
                Environment.GetEnvironmentVariable("GOOGLE_CREDENTIAL_JSON") ?? throw new InvalidOperationException("GOOGLE_CREDENTIAL_JSON is not set."));
            const int bodyLimit = 21 * 1024 * 1024; // 20MBくらいが trasnlateDocument の限界
            s_formOptions = new FormOptions
            {
                BufferBodyLengthLimit = bodyLimit,
                MultipartBodyLengthLimit = bodyLimit,
            };
            s_httpClient = new HttpClient()
            {
                Timeout = TimeSpan.FromSeconds(60),
            };
            s_jsonOptions = new JsonSerializerOptions()
            {
                IgnoreNullValues = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
        }

        [FunctionName("Translate")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            CancellationToken hostCancellationToken,
            ILogger log)
        {
            if ("get".Equals(req.Method, StringComparison.OrdinalIgnoreCase))
            {
                var resStream = typeof(Translate).Assembly.GetManifestResourceStream(typeof(Translate), "translate.html");
                return new FileStreamResult(resStream, "text/html;charset=utf-8");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken, req.HttpContext.RequestAborted);
            var ct = cts.Token;

            var form = await req.ReadFormAsync(s_formOptions, ct);
            string? sourceLang;
            string targetLang;
            try
            {
                sourceLang = form["sourcelang"].SingleOrDefault();
                targetLang = form["targetlang"].Single();
            }
            catch (InvalidOperationException)
            {
                return new BadRequestResult();
            }

            var docFile = form.Files["docfile"];
            if (docFile == null) return new BadRequestResult();

            var accessToken = await s_googleCredential.GetAccessTokenForRequestAsync("https://translate.googleapis.com/", ct);
            var requestUri = "https://translate.googleapis.com/v3beta1/projects/" + Uri.EscapeDataString(s_googleProjectId) + "/locations/global:translateDocument";

            var docContent = new byte[docFile.Length];
            await using (var docStream = docFile.OpenReadStream())
            {
                var index = 0;
                while (index < docContent.Length)
                {
                    var count = await docStream.ReadAsync(docContent, index, docContent.Length - index);
                    index += count;
                    if (count == 0 && index < docContent.Length)
                        throw new EndOfStreamException();
                }
            }

            var requestContent = JsonSerializer.SerializeToUtf8Bytes(
                new TranslateDocumentRequest()
                {
                    SourceLanguageCode = sourceLang,
                    TargetLanguageCode = targetLang,
                    DocumentInputConfig = new DocumentInputConfig()
                    {
                        MimeType = docFile.ContentType,
                        Content = docContent,
                    }
                },
                s_jsonOptions);

            var apiRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "https://translate.googleapis.com/v3beta1/projects/" + Uri.EscapeDataString(s_googleProjectId) + "/locations/global:translateDocument"
            )
            {
                Headers =
                {
                    Accept = { new MediaTypeWithQualityHeaderValue("application/json") {  CharSet = "utf-8" } },
                    Authorization = new AuthenticationHeaderValue("Bearer", accessToken),
                },
                Content = new ByteArrayContent(requestContent)
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" },
                    }
                }
            };

            var response = await s_httpClient.SendAsync(apiRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var resStr = await response.Content.ReadAsStringAsync();
                log.LogError("API returned {StatusCode} {Content}", (int)response.StatusCode, resStr);
                return new ContentResult()
                {
                    Content = "translateDocument returned an error.",
                    ContentType = "text/plain",
                    StatusCode = 500,
                };
            }

            var resObj = await JsonSerializer.DeserializeAsync<TranslateDocumentResponse>(
                await response.Content.ReadAsStreamAsync(), s_jsonOptions, ct);
            var translationResult = resObj.DocumentTranslation;

            var resultFileName = docFile.FileName;
            if (string.IsNullOrEmpty(resultFileName)) resultFileName = "document";
            var lastDotIndex = resultFileName.LastIndexOf('.');
            resultFileName = lastDotIndex < 0
                ? resultFileName + "." + targetLang
                : resultFileName.Substring(0, lastDotIndex) + "." + targetLang + resultFileName.Substring(lastDotIndex);

            return new FileContentResult(translationResult.ByteStreamOutputs[0], translationResult.MimeType)
            {
                FileDownloadName = resultFileName
            };
        }

#pragma warning disable CS8618 // null 非許容のフィールドには、コンストラクターの終了時に null 以外の値が入っていなければなりません。Null 許容として宣言することをご検討ください。
        private sealed class TranslateDocumentRequest
        {
            public string? SourceLanguageCode { get; set; }
            public string TargetLanguageCode { get; set; }
            public DocumentInputConfig DocumentInputConfig { get; set; }
        }

        private sealed class DocumentInputConfig
        {
            public string MimeType { get; set; }
            public byte[] Content { get; set; }
        }

        private sealed class TranslateDocumentResponse
        {
            public DocumentTranslation DocumentTranslation { get; set; }
        }

        private sealed class DocumentTranslation
        {
            public byte[][] ByteStreamOutputs { get; set; }
            public string MimeType { get; set; }
        }
#pragma warning restore CS8618 // null 非許容のフィールドには、コンストラクターの終了時に null 以外の値が入っていなければなりません。Null 許容として宣言することをご検討ください。
    }
}
