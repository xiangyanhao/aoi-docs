# Aoi Playground

基于 Blazor WebAssembly 的浏览器端 Aoi playground，可部署到 GitHub Pages。

本地开发：

```bash
dotnet run --project playground/Aoi.Playground
```

发布（项目站点，仓库名为 `aoi-docs`）：

```bash
# 将 base href 设为 /aoi-docs/
sed -i 's|<base href="/" />|<base href="/aoi-docs/" />|' playground/Aoi.Playground/wwwroot/index.html
dotnet publish playground/Aoi.Playground/Aoi.Playground.csproj -c Release -o publish
```

合并到 `master` 后，GitHub Actions workflow `.github/workflows/pages.yml` 会自动构建并部署。

首次启用时，在仓库 **Settings → Pages → Build and deployment → Source** 选择 **GitHub Actions**。
