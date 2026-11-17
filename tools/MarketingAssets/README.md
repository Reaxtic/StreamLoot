# Reproduce the marketing assets

These assets use the actual current WPF view markup with clearly labeled illustrative data, not live accounts. The renderer strips event handlers and the code-behind class. It does not start the application, mining engine or WebViews. Browser-backed images are replaced with the existing project logo. Any incidental image cache uses an isolated demo profile under the tool's ignored build output.

On Windows with .NET 10:

```powershell
dotnet run --project tools/MarketingAssets -- 'C:/path/to/StreamLoot'
```

This writes `Github-Assets/dashboard-current.png` and `inventory-current.png`. If view bindings change, update the illustrative model in `Program.cs` and visually inspect both images.

For the banner and social card, use Node.js and `sharp` from your tools environment:

```powershell
$env:NODE_PATH = 'C:/path/to/tools/node_modules'
node tools/MarketingAssets/render-card.cjs 'C:/path/to/StreamLoot'
```

This creates SVG sources and PNGs using the project's existing logo and demo screenshot. It does not invent game artwork, testimonials or live earnings. The social card is 1280 × 640 pixels and must remain under 1 MB for GitHub social preview.

Upload `social-preview.png` through **repository Settings → Social preview → Edit → Upload an image**. Committing the file alone does not configure that setting. Only claim that the preview is active after checking the repository setting or public Open Graph image.

Validate local Markdown links and issue-form YAML with Node.js and the `yaml` package from your separate tools environment:

```powershell
node tools/MarketingAssets/validate-docs.cjs 'C:/path/to/StreamLoot'
```
