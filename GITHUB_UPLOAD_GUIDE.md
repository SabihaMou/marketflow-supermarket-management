# Upload MarketFlow to GitHub

1. Extract the ZIP.
2. Open https://github.com/new and create a public repository named `marketflow-supermarket-management`. Do not initialize it with a README.
3. On the new repository page, select **uploading an existing file**.
4. Open the extracted `marketflow-github-ready` folder and drag its contents (not the enclosing folder) into the GitHub upload area. This includes `src`, `screenshots`, `README.md`, and `.gitignore`.
5. Commit with a message such as `Add MarketFlow source code and screenshots`.
6. Confirm the README renders all six screenshots and that `src/MarketApp/Program.cs` is present.

Note: GitHub's browser uploader may not display hidden `.gitignore` files in some workflows. It is optional for the initial upload but useful to keep build files and local databases out of Git. Never upload `market.db`, `bin`, or `obj` directories.

This is a reconstructed implementation based on an older project report, not recovered original source code. Preserve attribution for earlier team contributions where applicable.
