# Token 计岛（token-meter）

WinIsland / WinLand 灵动岛插件：实时显示本机 AI 会话的 **token 用量**与**估算费用**，支持自定义模型单价。

- **小岛常驻**：今日花费 + 今日总 token
- **展开**：输入/输出/缓存读拆解、今日逐模型前 3 名、本周/本月/累计费用；接了多个来源时每个来源各一行
- **点岛体（聚光卡）**：今日/本周/本月/累计 KPI、近 7 天柱状趋势（可切分组/堆叠）、今日逐模型完整明细、数据源信息（内容可滚动，不会被卡片截断）
- **设置页**：显示开关、刷新间隔、小岛常驻优先级、数据目录、本地日志的模型范围与今日预算、近 7 天图画法、货币符号、单价基数、自定义模型单价的增删改，以及**接口配置文件的路径、已加载接口清单、重新读取与生成示例**
- **接口配置不在设置页里改**：它是一份纯文本 `providers.json`，见下文

## 数据源：两个运营商分列统计，不互相覆盖

从 1.1.0 起有两个数据源，**各算各的、分列展示**：

1. **本机会话日志**：默认自动定位本机 AI 客户端的会话记录目录（插件自己算出来，不用你填；也可在设置页改成自己的目录，多个用 ; 分隔）。只统计 role=assistant 且带 usage 的记录。
2. **服务商用量接口**（可选，默认关）：接口不可用、超时、返回结构不对或权限不足时，**自动降级回本地日志**，并保留上一次成功拿到的服务端数据。

### 为什么不合并相加

两个来源往往是**不同的运营商**，统计的是两份互不相交的用量，而且**币种不同**（本地按人民币单价估算，网关可能直接报美元扣费）。把它们加在一起会得出一个看似精确、实际无意义的数字。

> 历史上这里踩过一个真实的坑：早期版本用「服务端覆盖本地整天」的合并规则，结果接入一个只有一天数据的网关后，**29 天的本地账本被压成了 1 天**。现在改成按模型归属分派，并有专门用例钉住这个回归。

### 归属怎么分（手动填）

| 设置项 | 在哪 | 含义 |
|---|---|---|
| 本地日志统计哪些模型 | 设置页 | 逗号分隔的子串，如 `gpt-, claude-`；**留空 = 除云端认领外的全部** |
| 本地日志今日预算 | 设置页 | 达到就在岛上标「超预算」。只提示，不影响任何数字 |
| 云端统计哪些模型 | `providers.json` 的 `ModelScope` | 逗号分隔的子串，如 `deepseek-, claude-`；**留空 = 上报什么就归什么** |
| 云端币种 | `providers.json` 的 `Currency` | 云端金额用什么符号显示（默认 `$`），与本地 `¥` 分列，不相加 |

分派规则：

- 模型只属于一个来源 → 直接归那边
- **同一个模型被两个以上接口同时上报 → 一个都不认**，改由本地日志显示，并把模型名和涉及的两家接口一起点名报出来，提示你给其中一家设 `ModelScope` 把它排除。
  这里刻意**不**用"先到先得"：那种做法归属由接口在文件里的排列顺序决定，换个顺序数字就从 A 家挪到 B 家，而界面上一切看起来正常 —— 一个静默改变数字归因的规则不能算对。
- 本地范围填了、而某模型不在范围内 → 两边都不统计，**并且会明确告知**（否则数字“凭空变少”无法解释）
- 服务端金额只覆盖部分数据时，该窗口标为「混合」并继续显示本地估算，不拿部分数据冒充全量
- 云端在它已认领的模型上报得**比本地少**且超过 `UndercountWarnPercent` → 标警告而不是静默覆盖（见下文的受限 Key 陷阱）

界面（岛面 / 聚光卡 / 设置页）都会写明当前口径与每个来源的币种。

### 网络与密钥

- 只向你配置的那一个地址发 GET 请求；除了这个接口不发任何其他网络请求。
- **发请求前会卡一次地址**（AssertSafeUrl）：必须是 http/https 绝对地址；带密钥时禁止走明文 http（本地调试用 127.0.0.1 / localhost 例外）。BaseUrl 填错等于把密钥送给别人，这里宁可不发。
- **密钥不保存在插件里**：配置文件与设置页里都只存一个**环境变量名**（`ApiKeyEnvVar`），运行时从环境变量读。因此密钥不会落进宿主的 settings.json、不会进 `providers.json`、不会进插件自己的缓存文件、也不会出现在错误文案里。
- 少数接口把密钥放在查询参数里（如 ?key=*** 形态），路径里写 `{key}` 即可，会做 URL 转义；`{base}` 可引用 BaseUrl。
- **识别「HTTP 200 但业务层报错」**：部分接口 key 不对也返回 200，真正的错误在 body 的 code/msg 或 error.message 里。不抱出来就会被当成「空数据」静默吞掉，所以插件会检查 success=false / ok=false / code 非 0 且带 msg / error 字段，并把原因报进日志。**非 2xx 时也会先读一次响应体**，把 401/403/404 里那句能定位问题的话提炼进提示（而不是甩给你一坨 JSON）。
- 取值路径支持数组下标（如 `balance_infos[0].total_balance`），也支持钻进「字符串里包的 JSON」（不少网关把扩展字段写成一段 JSON 文本）。`ValueScale` 用于接口返回单位不是「个」的情况。
- 接口失败只在插件日志里留一句原因（含 HTTP 状态码），不重试轰炸；下一轮刷新再试。

### 字段口径（最容易算错的地方）

不同接口对「输入是否包含缓存命中」的处理不一样，插件用一个开关 InputIncludesCached 控制：

- true：不命中缓存的输入 = InputField - CachedInputField，缓存读单独计
- false：两者互斥，直接相加

选错的后果是**缓存部分被重复计费或整段漏计**。默认值只是一个 OpenAI 风格的**示例起点，未对照任何官方文档核实**；窗口天数、分页字段、时间参数格式、字段路径全部在 `providers.json` 里改。

> 代码默认的 `input_tokens` / `input_cached_tokens` / `after` / `Authorization: Bearer` 只是示例写法，**不要当成 OpenAI 的事实**；要接任何一家都请先自己核对其字段名与限制再写进配置文件。

> 一个真实陷阱：很多用量接口**不覆盖账户下全部 Key**。受限 Key 或子账号可能只能看到自己创建的 Key 的用量，这类接口不会报错，只会少给数。所以插件在「服务端总量明显低于本地同期」时会**标警告而不是静默覆盖**，并提示你把它当低估值。

**费用一律由插件按你填的单价算**：

```
费用 = (输入×输入价 + 输出×输出价 + 缓存读×缓存读价 + 缓存写×缓存写价) / 单价基数
```

单价基数默认 1,000,000，也就是服务商价目表上「每 百万 tokens」的写法，照抄即可。
**没配单价的模型显示「未定价」**，不会被算成免费，也不会让总费用看起来正常。

## 接口配置：providers.json

云端接口**不在设置页里配**。它是一份放在插件目录下的纯文本文件，设置页只显示它的完整路径、已加载的接口清单，并提供「重新读取」和「生成示例配置」两个按钮。

这么设计的理由是可回退与可解释：一个 JSON 数组可以手改、可以备份、可以 diff、可以整份复制走；而"设置页里一张按 id 关联的表单"曾经因为一个按钮误触把其它接口抹掉过，那种失败没有任何文件能恢复。

- **文件是唯一真相**：插件迁移完成后**只读不写**这个文件，所以你加的注释、你排的格式都会留着。
- **允许注释**（按 JSONC 读）：`//` 行注释和尾逗号都可以写，每个字段旁边就能就地写说明。
- **保存即生效**：插件每秒比一次文件时间戳，改完存盘不用重启宿主。
- **读不通不会毁掉配置**：JSON 损坏时沿用上一次成功读到的那份，并且**绝不写回** —— 一个坏文件不该把你的好配置盖掉。
- **可以接多家**：数组里写几个 `{ }` 就接几家，各家的账本、水位线、币种、预算都是独立的一份。**数组里的顺序就是界面显示顺序**（岛面高度只够几行，写在前面的先显示，排在后面的会被折起来）。
- 设置页的「生成示例配置」会写出一份**每个字段都带一行说明**的模板（含一个用量型和一个余额型示例，后者默认关着）。它只在文件还不存在时才写，已存在直接拒绝。

一个最小可用长这样（`[ ]` 是数组，可以有多项）：

```jsonc
[
  {
    "Enabled": true,                       // false = 完全忽略这一项，只用本地日志
    "DisplayName": "我的网关",              // 界面上显示的名字
    "BaseUrl": "https://gateway.example",  // 结尾不要带 /
    "Path": "/api/log/self",
    "AuthHeader": "Authorization",
    "AuthPrefix": "",                      // 有的家要写 "Bearer "
    "ApiKeyEnvVar": "MY_GATEWAY_KEY",      // 【环境变量名】，不是密钥本身
    "ItemsPath": "data.items",
    "TimeField": "created_at",
    "ModelField": "model_name",
    "InputField": "prompt_tokens",
    "OutputField": "completion_tokens",
    "Currency": "$"
  }
]
```

⚠ **这个文件算个人数据**：里面有你的接口地址、路径和账号相关的需求头。它不随源码发布（已在 `.gitignore` 里），提 issue 或贴配置时请自己先脱敏。

### 余额型接口（Kind=balance）

有的家没有逐条用量日志，只有一个"账户还剩多少钱"的端点。把 `Kind` 写成 `balance`，再用 `BalanceField` 指到那个数：

```jsonc
{
  "Enabled": true,
  "Kind": "balance",                       // 关键：这一项只读一个总额
  "DisplayName": "我的账户余额",
  "BaseUrl": "https://gateway.example",
  "Path": "/api/user/self",
  "ApiKeyEnvVar": "MY_GATEWAY_KEY",
  "BalanceField": "data.quota",            // 支持点号、数组下标、钻进字符串里的 JSON；逗号分隔多个会求和
  "CostUnitDivisor": 500000,               // 金额换算率：字段值 ÷ 这个数 = 元/美元
  "Currency": "$",
  "BalanceWarnBelow": 5                    // 余额低于这个数就在岛上标「余额偏低」
}
```

它**只占界面上单独一行**，不进按天账本、也不进近 7 天柱状图 —— 余额是"此刻还剩多少"，不是"某天用了多少"。混进按天结构会同时犯两个错：柱状图里长出一根按总额刻度的假巨柱，并且同一笔余额每天被再计一次。这类接口也**不会**被拼上时间参数和分页参数（余额是快照，多带一个 `start_date` 常常直接被网关拒掉）。

### 预算与告警

| 字段 | 作用 |
|---|---|
| `DailyBudget` | 这一家的今日预算（按它自己的币种），达到就岛上标「超预算」 |
| `BalanceWarnBelow` | 余额预警线，低于它标「余额偏低」 |
| `UndercountWarnPercent` | 这一家上报的总量比本地同期少超过这个百分比时告警（多半是 Key 权限不全） |

三条都**只是提示，不参与任何数字的计算**。阈值填 0、负数或乱填一律当"没启用" —— 手抖填错不该变成任何用量都告警。日志按"内容变了才记一条"去重，全部解除时补一条恢复。

## 性能

扫描是**按文件字节偏移增量**的：每个文件只从上次停下的位置往后读，未写完的尾行不计入偏移（下次重读）。
在一台会话记录约 180 MB / 100 多个文件的机器上实测：首次全量扫描 < 1 秒，之后稳态重扫约 10 毫秒、新读 0 字节。
扫描进度持久化在插件自己目录下的 .cache\scan-cache-v1.json ——卸载插件时它随 plugins\<id>\ 一起被删，不留残留文件；仅当插件目录不可写时才退回 %LOCALAPPDATA%\token-meter\ ，并在日志里说明。删掉缓存等于下次从头重扫，设置页也有这个按钮。

解析器的正确性用另一套完全独立的实现（awk 正则）在同一份冻结数据上做过逐项交叉比对，
六项指标（含 usage 行数）完全一致。

## 市场审核对照

| 审核标准 | 本插件 |
|---|---|
| 能正常安装/启用/禁用/卸载，不残留后台进程与文件 | 无子进程、无自启；定时器与设置页由宿主托管；扫描缓存在插件目录 .cache\ 内，卸载即消失 |
| 不把宿主自带运行时打进包 | 不打包 WinIsland.Core.dll / WinUI / WinRT / WebView2 / *.pdb；与 submit-plugin.ps1 的过滤清单一致 |
| 不越权：只用自己的设置键 | 只用 enabled / priority / refreshSeconds / roots / pricing / localScope / localBudget / chartMode 八个键（SDK 自动加 token-meter. 前缀）；早先的 provider 键已停用，仅在接口配置文件还不存在时读一次做迁移。不碰 plugin.<id>.disabled，不读写其他插件的数据 |
| 说明与行为一致，许可证明确 | MIT（见 LICENSE）；接口会联网，因此清单与本文都明确写出「默认只读本地日志；启用接口后仅请求你所配置的那一个地址；密钥只从环境变量读」 |
| 版本号递增 | 首发 1.0.0；后续更新同时改 plugin.json 的 version 并重新打包 |

## 关于用量

本机全量实测（15,504 条带 usage 的记录）：**100% 满足 totalTokens = input + output + cacheRead + cacheWrite**，
证明日志里的 input **已经是不含缓存的那部分**，与 cacheRead 互斥——本地计费不会重复计算缓存 token。
其中 96.7% 的记录 cacheRead > 0，「缓存读单价」对总费用影响极大。

## 关于服务商用量接口：接之前先核实

不是所有服务商都有可编程的用量 / 账单查询接口。部分平台只提供网页版账单入口，模型列表接口也只给模型标识而不含价格；这种情况下「服务端优先」没有可用对象，应保持本地日志模式（也就是默认状态）。

接任何一家之前先确认三件事：

1. 它到底有没有用量 / 账单查询端点，而不是只有控制台页面；
2. 你手上这把 Key 的权限能覆盖多少来源——受限 Key 常常只能看到自己创建的 Key 的用量，这类接口不报错、只会少给数；
3. 单价能不能从接口拿到。拿不到就得手工填，而「缓存命中输入」的单价方向务必核对（通常低于输入价），填反一个量级，总费用就会差十倍。

本插件不依赖以上任何条件也能工作：接口不可用、没配、或权限不够时，一律降级到本地会话记录，界面会标出当前口径。

## 已核实的接口写法

下面这些字段值是**照着各家官方文档或实测响应核过的**，可以直接抄进 `providers.json` 的数组里（注意整个文件是一个数组，每家是其中一个 `{ }`）。抄完仍要自己填 `BaseUrl` 和把密钥设进对应的环境变量 —— 配置文件里只写**环境变量名**，不写密钥。

### Anthropic 组织用量

```json
{
  "Enabled": true,
  "DisplayName": "Anthropic 组织用量",
  "BaseUrl": "https://api.anthropic.com",
  "Path": "/v1/organizations/usage_report/messages",
  "AuthHeader": "x-api-key",
  "AuthPrefix": "",
  "ApiKeyEnvVar": "ANTHROPIC_ADMIN_KEY",
  "ExtraHeaders": "anthropic-version=2023-06-01",
  "StartParam": "starting_at",
  "EndParam": "ending_at",
  "TimeFormat": "rfc3339",
  "ExtraQuery": "bucket_width=1d&group_by[]=model",
  "ItemsPath": "data[].results",
  "TimeField": "starting_at",
  "ModelField": "model",
  "ModelFallback": "未分组",
  "InputField": "uncached_input_tokens",
  "OutputField": "output_tokens",
  "CachedInputField": "cache_read_input_tokens",
  "CacheWriteField": "cache_creation.ephemeral_5m_input_tokens,cache_creation.ephemeral_1h_input_tokens",
  "RequestsField": "",
  "InputIncludesCached": false,
  "CostField": "",
  "PageTokenField": "next_page",
  "PageParam": "page",
  "HasMoreField": "has_more",
  "LimitParam": "limit",
  "LimitPerRequest": 31,
  "WindowMaxDays": 31,
  "LagMinutes": 10,
  "TimeoutSeconds": 15,
  "MaxPagesPerWindow": 50,
  "MaxWindowsPerRun": 12,
  "InitialBackfillDays": 7,
  "UndercountWarnPercent": 10
}
```

为什么这些值长这样（全部来自官方文档）：

| 字段 | 官方依据 |
|---|---|
| x-api-key + anthropic-version | 鉴权走 x-api-key，且必须带版本头；**Admin API 对个人账户不可用**，workspace key 也不行，只能用 Admin key 或未绑定 workspace 的个人/服务账号 key |
| data[].results + TimeField=starting_at | 响应是两层：data[] 是时间桶（带 starting_at/ending_at），明细在桶内的 results[] 里 |
| InputIncludesCached=false | 官方直接给 uncached_input_tokens 与 cache_read_input_tokens，两者不重叠 |
| CacheWriteField 逗号分隔两项 | 缓存写分 ephemeral_5m_input_tokens 与 ephemeral_1h_input_tokens 两档，要求和 |
| WindowMaxDays=31 / LimitPerRequest=31 | 官方限制表：1d 默认 7 个桶、**最大 31 个**（1h 24/168，1m 60/1440） |
| page + has_more + next_page | 官方分页流程：has_more 为 true 就把 next_page 当作 page 参数继续请求 |
| LagMinutes=10 | 官方称数据通常在请求完成后 5 分钟出现，建议轮询频率 1 次/分钟 |

两个已知缺口：group_by[]=model 才能按模型拆分；**服务端不给请求次数**（RequestsField 留空），所以混合口径下「请求数」仍只来自本地日志。另：Priority Tier 的费用不在 cost 端点里，得用 usage 端点。

> 配置文件不写注释也合法，但建议给难懂的字段留一行 `//` 说明 —— 这文件是 JSONC 读的，插件不会再重写它，所以注释会一直留着。

### New API / One API 网关（逐请求日志）

很多中转站、公益站都是 New API 改的（看 api/status 能认出来）。这类网关的用量日志取值：

| 配置 | 值 | 为什么 |
|---|---|---|
| Path | /api/log/self | 逐请求消耗日志（ExtraQuery 写 type=2 只看消耗） |
| AuthHeader / AuthPrefix | Authorization / 空 | 控制台接口用**裸令牌**，不带 Bearer |
| ExtraHeaders | New-Api-User=<你的用户ID> | **少了这个头直接 401**；ID 在控制台头像旁 |
| ApiKeyEnvVar | 存「系统访问令牌」的变量名 | 不是 sk- 推理 key，两者不通用 |
| ItemsPath | data.items | 条目嵌在 data 对象里 |
| TimeField / TimeFormat | created_at / unix | 时间是 **unix 秒** |
| CachedInputField | other.cache_tokens | other 是**字符串里包的 JSON**，插件会自动解开再取 |
| CostField / CostUnitDivisor | quota / 500000 | New API 记账单位，**500000 = $1** |
| PageMode | page（page/per_page/data.total） | 它没有 next_page 游标，只能按页码翻 |

实测对照：quota ÷ 500000 算出的余额与控制台页面**完全一致**；
而 other 里的系数直接回答了「缓存该多少钱」：cache_ratio=0.5（缓存读是输入价的 50%）、
cache_creation_ratio=1.25（缓存写 125%）、completion_ratio=3（输出是输入的 3 倍）。

这类网关通常**同时**有余额端点，所以很多人会两家一起接：一个 `Kind=usage` 的条目读
`/api/log/self` 的逐条用量，再写一个 `Kind=balance` 的条目读 `/api/user/self` 的
`data.quota` 当余额（同一个令牌、同一个 `New-Api-User` 头、同样 ÷500000 = 美元）。
余额那条只会显示成单独一行，不进按天账本。

⚠️ 示例里 InputIncludesCached=true（即 prompt_tokens 已包含缓存部分）。
这是 New API 惯例，如果账号里**还没出现过 cache_tokens > 0 的样本**，无法用数据验证。
接上后请拿一天的服务端 quota 总额与网关账单对一次；对不上就把这个开关改成 false 再看。

⚠️ 隐私：`/api/user/self` 的响应里**含邮箱、邀请码等账号字段**。插件按 `BalanceField`
指向的那一个路径取值（例子里是 `data.quota`），**不会把整包响应写进日志或缓存**，
也不显示除那个数以外的任何字段。但请求确实会带着你的令牌打到该地址 —— 不想让插件碰这个
端点的话，就不要写 `Kind=balance` 那一条，用量与本地日志功能不受影响。
用量端点 `/api/log/self` 同理：只取时间、模型名、三个 token 数与 quota。

## 致谢与代码来源

接口层的四个实现思路——**声明式厂商模板 + JSON 路径取值（含数组下标）+ scale 乘数**、**从 2xx 响应体里提炼业务层错误**、**发请求前的地址安全卡控**、**金额用 1e-8 定点整数记账（避免浮点累加误差）**——参考自 [MeteorNOX/DeepSeek-Balance-Whale-Widget](https://github.com/MeteorNOX/DeepSeek-Balance-Whale-Widget)（其 lib/ 部分为 MIT 许可，作者已同意复用这几个实现方法）。

本仓库只按这些思路用 C# 重新实现，**没有复制它的任何文件**，也没有使用其 assets/ 与 icon.png（按其 PROVENANCE.md，那部分不在 MIT 覆盖范围内）。

## 已知边界

- 服务端接口的默认字段名与限制**未对照官方文档核实**，只是示例起点；接不同服务商前必须自己校对（尤其是 InputIncludesCached）。
- 接口配置是**手改文件**，没有表单校验：填错字段名的表现通常是"数字偏低或拉不到数据"而不是报错，所以接一家先小范围试一天。设置页会显示它到底加载了几家。
- 近 7 天图的柱高用 token 数做**共享刻度**（币种之间不可比，也不能相加）。因此当两个来源的量级差很大时（例如本地每天几亿 token、某家网关一天几万），**小那一家在图上几乎看不见，换堆叠也一样** —— 这是共享刻度的必然结果，不是排布问题。它的金额仍会单独显示在该列的标签里，没丢。
- 岛面展开高度是固定的：接的来源越多，逐模型明细显示的行数就越少（每多一个来源让出一行），全部来源的完整信息都在聚光卡里。
- 按天分桶用本地时区；跨时区后历史记录仍按当初的本地日期归桶。
- 只统计 role=assistant 的记录，用户消息不计费（服务商也只为助手输出计费）。
- reasoning（思考 token）不单独累加 —— 它已含在 output 里，再加会重复计费。
- 费用是**估算**：以你填的单价为准，与服务商实际账单可能有出入（闲忙时价、套餐抵扣等不在考虑范围内）。
