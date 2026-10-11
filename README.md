# Token 计岛（token-meter）

WinIsland / WinLand 灵动岛插件：实时显示本机 AI 会话的 **token 用量**与**估算费用**，支持自定义模型单价。

- **小岛常驻**：今日花费 + 今日总 token
- **展开**：输入/输出/缓存读拆解、今日逐模型前 3 名、本周/本月/累计费用
- **点岛体（聚光卡）**：今日/本周/本月/累计 KPI、近 7 天柱状趋势、今日逐模型完整明细、数据源信息（内容可滚动，不会被卡片截断）
- **设置页**：显示开关、刷新间隔、小岛常驻优先级、数据目录、货币符号、单价基数、自定义模型单价的增删改、**服务端用量接口开关与字段映射**

## 数据源与优先级

从 1.1.0 起有两个数据源，**服务端优先、本地日志兜底**：

1. **服务商用量接口**（可选，默认关）：启用后用于计费与跳设备汇总；接口不可用、超时、返回结构不对或权限不足时，**自动降级回本地日志**，并保留上一次成功拿到的服务端数据。
2. **本机会话日志**：%USERPROFILE%\.sclaw\agent\projects\**\*.jsonl（目录可在设置页改，多个用 ; 分隔）。只统计 role=assistant 且带 usage 的记录。

两边怎么呌：服务端数据只信到「现在 - 滞后分钟」为止，**滞后带以内（最近一段时间）继续用本地日志**，因为多数用量接口对当天的数据是残缺的。界面上会明写当前口径是「本地日志 / 服务端 / 混合」，避免接口静默降级时你看到的数字口径变了却不知道。

### 网络与密钥

- 只向你配置的那一个地址发 GET 请求；除了这个接口不发任何其他网络请求。
- **密钥不保存在插件里**：设置页只存一个**环境变量名**，运行时从环境变量读。因此密钥不会落进宿主的 settings.json、不会进插件自己的缓存文件、也不会出现在错误文案里。
- 接口失败只在插件日志里留一句原因（含 HTTP 状态码），不重试轰炸；下一轮刷新再试。

### 字段口径（最容易算错的地方）

不同接口对「输入是否包含缓存命中」的处理不一样，插件用一个开关 InputIncludesCached 控制：

- true：不命中缓存的输入 = InputField - CachedInputField，缓存读单独计
- false：两者互斥，直接相加

选错的后果是**缓存部分被重复计费或整段漏计**。默认值只是一个 OpenAI 风格的**示例起点，未对照任何官方文档核实**；窗口天数、分页字段、时间参数格式、字段路径全部可在设置页的「完整配置 JSON」里改。

> 一个真实陷阱：很多用量接口**不覆盖账户下全部 Key**。受限 Key 或子账号可能只能看到自己创建的 Key 的用量，这类接口不会报错，只会少给数。所以插件在「服务端总量明显低于本地同期」时会**标警告而不是静默覆盖**，并提示你把它当低估值。

会话记录里的 cost 字段全是 0，所以**费用一律由插件按你填的单价算**：

```
费用 = (输入×输入价 + 输出×输出价 + 缓存读×缓存读价 + 缓存写×缓存写价) / 单价基数
```

单价基数默认 1,000,000，也就是服务商价目表上「每 百万 tokens」的写法，照抄即可。
**没配单价的模型显示「未定价」**，不会被算成免费，也不会让总费用看起来正常。

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
| 不越权：只用自己的设置键 | 只用 enabled / priority / refreshSeconds / roots / pricing / provider 六个键（SDK 自动加 token-meter. 前缀）；不碰 plugin.<id>.disabled，不读写其他插件的数据 |
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

## 已核实的接口预设

下面这份是 **Anthropic Usage & Cost Admin API** 的可用配置，字段与限制已对官方文档核实。直接贴到设置页的「完整配置 JSON」里即可：

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

> 代码默认的 input_tokens / input_cached_tokens / after / Authorization: Bearer 只是示例写法，**不要当成 OpenAI 的事实**；要接 OpenAI 请先自己核对其字段名与限制再改预设。

## 已知边界

- 服务端接口的默认字段名与限制**未对照官方文档核实**，只是示例起点；接不同服务商前必须自己校对（尤其是 InputIncludesCached）。
- 按天分桶用本地时区；跨时区后历史记录仍按当初的本地日期归桶。
- 只统计 role=assistant 的记录，用户消息不计费（服务商也只为助手输出计费）。
- reasoning（思考 token）不单独累加 —— 它已含在 output 里，再加会重复计费。
- 费用是**估算**：以你填的单价为准，与服务商实际账单可能有出入（闲忙时价、套餐抵扣等不在考虑范围内）。
