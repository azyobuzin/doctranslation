# DocTranslation

A frontend for `translateDocument` API of Google Cloud Translation, deployed on Azure Functions.

![Screenshot](https://cdn-ak.f.st-hatena.com/images/fotolife/a/azyobuzin/20210602/20210602161018.png)

## How to deploy

1. Make a Azure Functions resource
2. Set the environment variables
    - `GOOGLE_PROJECT_ID`
    - `GOOGLE_CREDENTIAL_JSON`: content of the key JSON file for the service account
3. `func azure functionapp publish`
