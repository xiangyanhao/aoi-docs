# Aoi Playground

基于 Blazor WebAssembly 的浏览器端 Aoi playground，可部署到 GitHub Pages。

功能：示例运行、Share 链接、浅色/深色主题、Aoi 语法高亮、带行号的可拖拽编辑器、语言文档浏览与可运行示例。

运行防护：调用深度上限（512）与求值步数上限（200 万），递归栈溢出、死循环、除零等会返回可读错误而不是卡死页面。

文档：`1-` / `2-` / `3-` / `4-` 章节会在每次 build/publish 时通过 `playground/tools/sync-docs.py` 同步进 `wwwroot/doc-content/`（刻意不用 `docs/`，以免和 Blazor 路由 `/docs` 在 GitHub Pages 上冲突）；` ```aoi ` 代码块带高亮。可运行示例提供 **Run** / **Edit**；语法示意（省略号、占位符、尚未实现的泛型/特征等）只展示为 syntax，避免点了 Run 却报错。

本地开发：

```bash
python3 playground/tools/sync-docs.py   # 可选；dotnet build 也会自动跑
dotnet run --project playground/Aoi.Playground
```

发布（项目站点，仓库名为 `aoi-docs`）：

```bash
# 将 base href 设为 /aoi-docs/
sed -i 's|<base href="/" />|<base href="/aoi-docs/" />|' playground/Aoi.Playground/wwwroot/index.html
dotnet publish playground/Aoi.Playground/Aoi.Playground.csproj -c Release -o publish
```

合并到 `master` 后，GitHub Actions workflow `.github/workflows/pages.yml` 会自动构建并部署。部署时会给 `css/app.css` / `js/playground.js` 加上 commit SHA 查询参数，避免浏览器继续用旧缓存（例如播放器从 iframe 换成自建控件后样式错乱）。

首次启用时，在仓库 **Settings → Pages → Build and deployment → Source** 选择 **GitHub Actions**。

若本地仍看到旧 UI，对页面做一次强制刷新（Ctrl/⌘+Shift+R）即可。
