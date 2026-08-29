# gh-pages —— VelaShell 官网

这个分支是 <https://joesdu.github.io/VelaShell/> 的**发布目录**,不是源码分支:
`index.html` 就是站点本身,没有构建步骤,推上来即生效。

```
index.html                    站点(内联 CSS/JS,单文件)
assets/velashell.png          应用图标,兼作 favicon 与 logo
assets/social-preview.png     og:image,取自 build/social-preview/
.nojekyll                     关掉 Jekyll,按静态文件原样发布
```

## 改站点

直接改 `index.html` 再推本分支即可 —— main 受 ruleset 保护要走 PR,本分支不受限,
改文案不必开 PR。

配色、字体、辉光与网格参数取自 main 分支的
[`build/social-preview/social-preview.html`](https://github.com/joesdu/VelaShell/blob/main/build/social-preview/social-preview.html)。
那份是社交预览图的可重生成源,**两边共用一套品牌参数**:只改一边,官网与 GitHub 卡片会漂。

## 下载区不写死链接

Release 资产名带版本号(`VelaShell-1.4.2-win-x64.zip`),写死的话每发一版就断。
站点运行时读 `api.github.com/repos/joesdu/VelaShell/releases/latest`,填版本号与各平台直链;
接口取不到(离线、限流)就退回 `releases/latest` 页面,按钮始终可用 —— 所以发新版**不用动这个分支**。
