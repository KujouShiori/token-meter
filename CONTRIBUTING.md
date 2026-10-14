# 分支与发布规则

适用于本仓库（Token 计岛 / `token-meter`）。目的只有一个：**任何一个 ref 都能一眼看出它对应哪一版、
是不是已经发布出去**，避免出现"仓库写着 1.1.0、市场里装的其实是 1.0.0"这种对不上的情况。

## 分支

| 分支 | 作用 | 规则 |
|---|---|---|
| `main` | 开发主线，始终是可构建、可发布的最新状态 | 只接受 `feature/*` 合并；不直接在 main 上做实验性改动 |
| `feature/<主题>` | 单个功能或修复的开发分支 | 从 `main` 切出；合进 `main` 后**立即删除**，不留长期分支 |
| `releases/<版本号>` | 已经提交到社区市场的那一版的冻结线 | 从发布时的 tag 切出；**只修 bug，不演新功能**；市场要求改稿就在这里改并重新打 tag |

不允许出现 `1.1.0`、`beta_1.1.0` 这类**分支与 tag 混用、命名不带前缀**的形态——
它们看不出是分支还是标签、也看不出是不是已发布，正是之前踩过的混乱来源。

## 标签

- 格式固定为 **`v<主>.<次>.<修订>`**，例如 `v1.0.0`、`v1.1.0`。
- 每个 tag 指向"该版本构建产物（`.lwp`）与 `plugin.json` 完全一致"的那个提交。
- **tag 一旦推到远程就不移动**。要改就删掉重建并同步升版本号，绝不 `force push` 覆盖已有 tag——
  别人已经 clone 过的话，移动的 tag 会让他们的历史对不上。

## 版本号

`plugin.json` 的 `version` 是唯一权威版本号，规则：

1. 开始一个新版本的功能开发时，**先升版本号再提交**（`1.0.0 → 1.1.0`），
   这样任何时刻 `main` 上的版本号都大于等于已发布版本，不会混淆。
2. 新增功能升次版本（`1.0.0 → 1.1.0`），只修 bug 升修订号（`1.1.0 → 1.1.1`）。
3. **版本号只增不减**。社区市场靠它判断"有没有新版本"，回退会导致用户永远收不到更新。
4. 提交市场前必须确认：`plugins/<id>/plugin.json` 与 `.lwp` 包内那份**逐字节一致**
   （官方 `submit-plugin.ps1` 会校验，`tools/` 脚本重建时也要复核）。

## 发布一次版本的完整流程

```bash
# 1) 在 feature 分支上完成开发，版本号已升好，本地实测通过
git checkout main && git merge --ff-only feature/<主题>
git push origin main

# 2) 打 tag（指向确定要发布的那个提交）
git tag v1.1.0
git push origin v1.1.0

# 3) 切出发布线，供市场后续改稿使用
git branch releases/1.1.0 v1.1.0
git push origin releases/1.1.0

# 4) 删除已合并的 feature 分支
git branch -d feature/<主题>
git push origin --delete feature/<主题>

# 5) 构建投稿包并投稿市场（用官方脚本，索引由上游 CI 重建）
dotnet build -c Release
#    → tools/submit-plugin.ps1 -SourceDir bin/Release/... -Readme README.md -Logo logo.png -NoIndex
```

`logo.png` 是**市场列表用的图标**（240×240 透明 PNG），它和 `.lwp` 平级放在市场的
`plugins/<id>/` 里，**不打进插件包** —— 所以本仓库只在根目录存一份，构建输出里不需要它。
岛体上那个符号走的是 `plugin.json` 的 `icon_glyph`（Segoe 字形），与市场图标是两回事，
换 logo 不影响岛上显示。

## 投稿市场与仓库的关系

- **市场里装的版本 = 某个 `releases/<版本号>` 分支的头**，不是 `main`。
  `main` 可以比市场超前（正在开发下一版），但市场里绝不能出现仓库没有对应 tag 的版本。
- 投稿内容（`plugins/<id>/`）从对应 tag 构建，避免把 `main` 上未完成的改动一起打包出去。
- 若官方审核要求改稿：在 `releases/<版本号>` 上改 → 重新构建 `.lwp` → 同一 PR 分支 push，
  PR 自动更新，不需要重开。

## 隐私约束（本仓库特有）

- `Directory.Build.props`（含本机 WinIsland 安装路径）、`bin/`、`obj/`、`dist/`、`.cache/`
  一律不入库，已在 `.gitignore` 中。
- 文档与代码注释里不写本机绝对路径、用户名、账号邮箱；提交作者邮箱统一用
  GitHub 匿名回复邮箱（`<id>+<login>@users.noreply.github.com`）。
- 任何 API 密钥都不进仓库、不进插件设置：只保存**环境变量名**，运行时从环境变量读。
