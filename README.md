# Token 计岛（token-meter）

WinIsland / WinLand 灵动岛插件：实时显示本机 AI 会话的 token 用量和估算费用，支持自定义模型单价。默认只读本地日志，不联网。

## 功能

- 小岛常驻：今日花费 + 今日总 token
- 展开：输入 / 输出 / 缓存读拆解，今日逐模型前 3 名，本周 / 本月 / 累计费用；接了多个来源时每个来源各一行
- 点岛体（聚光卡）：今日 / 本周 / 本月 / 累计 KPI，近 7 天柱状趋势（可切分组 / 堆叠），今日逐模型完整明细，数据源信息，内容可滚动
- 设置页：显示开关、刷新间隔、小岛常驻优先级、数据目录、统计哪些模型、今日预算、近 7 天图画法、货币符号、单价基数、自定义模型单价增删改，以及接口配置文件的路径、已加载接口清单、重新读取与生成示例
- 接口配置是一份纯文本 providers.json，见下文

## 数据源：两个来源分列统计，不互相覆盖

1. 本机会话日志（默认开启）：自动定位本机 AI 客户端的会话记录目录，也可在设置页改成自己的目录，多个用分号分隔。只统计 role 为 assistant 且带 usage 的记录。
2. 服务商用量接口（可选，默认关闭）：接口不可用、超时、返回结构不对或权限不足时，自动降级回本地日志，并保留上一次成功拿到的服务端数据。

两个来源往往是不同的运营商，统计的是两份互不相交的用量，币种也可能不同，所以不合并相加。归属按模型逐条分派：谁上报了哪个模型才归谁。

### 归属怎么分

设置页里控制本地日志统计哪些模型（逗号分隔的子串，留空表示除云端认领外的全部），以及本地日志今日预算（达到就标超预算，只提示，不影响数字）。

providers.json 里控制云端统计哪些模型（ModelScope，留空表示上报什么就归什么），以及云端币种（Currency，默认美元符号，与本地人民币分列）。

分派规则：

- 模型只属于一个来源，直接归那边
- 同一个模型被两个以上接口同时上报，一个都不认，改由本地日志显示，并把模型名和涉及的两家接口一起点名报出来，提示你给其中一家设 ModelScope 排除
- 本地范围填了而某模型不在范围内，两边都不统计，并且会明确告知
- 服务端金额只覆盖部分数据时，该窗口标为混合，继续显示本地估算
- 云端在它已认领的模型上报得比本地少且超过 UndercountWarnPercent，标警告而不是静默覆盖

界面都会写明当前口径与每个来源的币种。

## 网络与密钥

- 只向你配置的那一个地址发 GET 请求，不发任何其他网络请求
- 发请求前会卡一次地址，必须是 http/https 绝对地址；带密钥时禁止走明文 http，本地调试用 127.0.0.1 或 localhost 例外
- 密钥不保存在插件里：配置文件和设置页里都只存一个环境变量名（ApiKeyEnvVar），运行时从环境变量读。密钥不会落进宿主的 settings.json、不会进 providers.json、不会进插件缓存、也不会出现在错误文案里
- 少数接口把密钥放在查询参数里，路径里写花括号 key 即可，会做 URL 转义
- 识别 HTTP 200 但业务层报错的情况，会把 code、msg、error 里的原因报进日志；非 2xx 时也会先读响应体，把 401、403、404 里能定位问题的话提炼进提示
- 取值路径支持数组下标，也支持钻进字符串里包的 JSON；ValueScale 用于接口返回单位不是个的情况
- 接口失败只在插件日志里留一句原因，不重试轰炸，下一轮刷新再试

### 字段口径

不同接口对输入是否包含缓存命中的处理不一样，插件用一个开关 InputIncludesCached 控制：true 表示不命中缓存的输入等于 InputField 减 CachedInputField，缓存读单独计；false 表示两者互斥，直接相加。选错会导致缓存部分被重复计费或整段漏计。

默认值只是示例起点，未对照任何官方文档核实，接任何一家都请先核对其字段名与限制。

很多用量接口不覆盖账户下全部 Key，受限 Key 或子账号可能只能看到自己创建的 Key 的用量，不报错只会少给数。插件在服务端总量明显低于本地同期时会标警告而不是静默覆盖。

### 费用怎么算

费用一律由插件按你填的单价算：

费用 = (输入×输入价 + 输出×输出价 + 缓存读×缓存读价 + 缓存写×缓存写价) / 单价基数

单价基数默认 1,000,000，也就是服务商价目表上每百万 tokens 的写法，照抄即可。没配单价的模型显示未定价，不会被算成免费。

## 接口配置：providers.json

云端接口配置是一份放在插件目录下的纯文本文件。设置页只显示它的完整路径、已加载的接口清单，并提供重新读取和生成示例配置两个按钮。

- 文件是唯一真相：插件迁移完成后只读不写，你加的注释和格式都会留着
- 允许注释，按 JSONC 读，行注释和尾逗号都可以写
- 保存即生效，插件每秒比一次文件时间戳，不用重启宿主
- 读不通不会毁掉配置：JSON 损坏时沿用上一次成功读到的那份，且绝不写回
- 可以接多家，数组里写几个大括号就接几家，各家的账本、水位线、币种、预算都是独立的
- 数组里的顺序就是界面显示顺序，岛面高度只够几行，排在后面的会被折起来
- 设置页的生成示例配置只在文件还不存在时才写，已存在直接拒绝

一个最小可用长这样：

    [
      {
        "Enabled": true,
        "DisplayName": "我的网关",
        "BaseUrl": "https://gateway.example",
        "Path": "/api/log/self",
        "AuthHeader": "Authorization",
        "AuthPrefix": "",
        "ApiKeyEnvVar": "MY_GATEWAY_KEY",
        "ItemsPath": "data.items",
        "TimeField": "created_at",
        "ModelField": "model_name",
        "InputField": "prompt_tokens",
        "OutputField": "completion_tokens",
        "Currency": "$"
      }
    ]

这个文件算个人数据，里面有你的接口地址、路径和账号相关的需求头。它不随源码发布，已在 gitignore 里，提 issue 或贴配置时请自己先脱敏。

### 余额型接口

有的提供商没有逐条用量日志，只有一个账户还剩多少钱的端点。把 Kind 写成 balance，再用 BalanceField 指到那个数：

    {
      "Enabled": true,
      "Kind": "balance",
      "DisplayName": "我的账户余额",
      "BaseUrl": "https://gateway.example",
      "Path": "/api/user/self",
      "ApiKeyEnvVar": "MY_GATEWAY_KEY",
      "BalanceField": "data.quota",
      "CostUnitDivisor": 500000,
      "Currency": "$",
      "BalanceWarnBelow": 5
    }

它只占界面上单独一行，不进按天账本、也不进近 7 天柱状图。余额是此刻还剩多少，不是某天用了多少，混进按天结构会让柱状图长出一根假巨柱，并且同一笔余额每天被再计一次。这类接口也不会被拼上时间参数和分页参数。

### 预算与告警

DailyBudget 是这一家的今日预算，达到就标超预算。BalanceWarnBelow 是余额预警线。UndercountWarnPercent 是这一家上报总量比本地同期少超过这个百分比时告警。三条都只是提示，不参与任何数字的计算。阈值填 0、负数或乱填一律当没启用。

## 性能

扫描是按文件字节偏移增量的，每个文件只从上次停下的位置往后读。在一台会话记录约 180 MB、100 多个文件的机器上实测：首次全量扫描不到 1 秒，之后稳态重扫约 10 毫秒、新读 0 字节。

扫描进度持久化在插件自己目录下的 cache 文件里，卸载插件时随插件目录一起被删。仅当插件目录不可写时才退回 LOCALAPPDATA，并在日志里说明。设置页有清空缓存的按钮。

解析器的正确性用另一套完全独立的 awk 正则实现在同一份冻结数据上做过逐项交叉比对，六项指标完全一致。

## 市场审核对照

- 能正常安装、启用、禁用、卸载，不残留后台进程与文件：无子进程、无自启，扫描缓存在插件目录内，卸载即消失
- 不把宿主自带运行时打进包：不打包 WinIsland.Core.dll、WinUI、WinRT、WebView2、pdb
- 不越权，只用自己的设置键：只用 enabled、priority、refreshSeconds、roots、pricing、localScope、localBudget、chartMode 八个键；若接口配置文件还不存在，会读一次宿主设置里的 provider 键把内容迁过去，此后不再读写；不碰其他插件的数据
- 说明与行为一致，许可证明确：MIT
- 版本号递增，唯一权威来源是 plugin.json 的 version

## 关于用量

本机全量实测 15,504 条带 usage 的记录：100% 满足 totalTokens = input + output + cacheRead + cacheWrite，证明日志里的 input 已经是不含缓存的那部分，与 cacheRead 互斥。其中 96.7% 的记录 cacheRead 大于 0，缓存读单价对总费用影响极大。

## 接服务商接口前先核实

不是所有服务商都有可编程的用量或账单查询接口。接任何一家之前先确认三件事：

1. 它到底有没有用量或账单查询端点，而不是只有控制台页面
2. 你手上这把 Key 的权限能覆盖多少来源，受限 Key 常常只能看到自己创建的 Key 的用量
3. 单价能不能从接口拿到，拿不到就得手工填，缓存命中输入的单价方向务必核对

接口不可用、没配或权限不够时，一律降级到本地会话记录。

## 已核实的接口写法

下面这些字段值是照着各家官方文档或实测响应核过的，可以直接抄进 providers.json 的数组里。抄完仍要自己填 BaseUrl 和把密钥设进对应的环境变量。

### Anthropic 组织用量

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

两个已知缺口：group_by 必须带 model 才能按模型拆分；服务端不给请求次数，所以混合口径下请求数仍只来自本地日志。

### New API / One API 网关

很多中转站、公益站都是 New API 改的，看 api/status 能认出来。这类网关的用量日志取值：

- Path：/api/log/self，逐请求消耗日志
- AuthHeader 用 Authorization，AuthPrefix 留空，控制台接口用裸令牌，不带 Bearer
- ExtraHeaders 必须加 New-Api-User 等于你的用户 ID，少了这个头直接 401
- ApiKeyEnvVar 存系统访问令牌的变量名，不是 sk- 推理 key
- ItemsPath：data.items
- TimeField 用 created_at，TimeFormat 用 unix
- CachedInputField：other.cache_tokens，other 是字符串里包的 JSON，插件会自动解开再取
- CostField 用 quota，CostUnitDivisor 用 500000，即 500000 等于 1 美元
- PageMode 用 page，它没有 next_page 游标，只能按页码翻

实测 quota 除以 500000 算出的余额与控制台页面完全一致。这类网关通常同时有余额端点，可以再写一个 Kind 为 balance 的条目读 /api/user/self 的 data.quota 当余额，只会显示成单独一行，不进按天账本。

示例里 InputIncludesCached 为 true，即 prompt_tokens 已包含缓存部分，这是 New API 惯例。接上后请拿一天的服务端 quota 总额与网关账单对一次，对不上就把这个开关改成 false 再看。

隐私提醒：/api/user/self 的响应里含邮箱、邀请码等账号字段，插件按 BalanceField 指向的那一个路径取值，不会把整包响应写进日志或缓存。不想让插件碰这个端点的话，就不要写 balance 那一条。

## 致谢与代码来源

接口层的四个实现思路——声明式厂商模板加 JSON 路径取值、从 2xx 响应体里提炼业务层错误、发请求前的地址安全卡控、金额用 1e-8 定点整数记账——参考自 MeteorNOX/DeepSeek-Balance-Whale-Widget。本仓库只按这些思路用 C# 重新实现，没有复制它的任何文件，也没有使用其 assets 与 icon。

## 已知边界

- 服务端接口的默认字段名与限制未对照官方文档核实，只是示例起点，接不同服务商前必须自己校对
- 接口配置是手改文件，没有表单校验，填错字段名通常表现为数字偏低或拉不到数据而不是报错
- 近 7 天图的柱高用 token 数做共享刻度，两个来源量级差很大时小那一家几乎看不见，换堆叠也一样，这是共享刻度的必然结果
- 岛面展开高度固定，接的来源越多，逐模型明细显示的行数越少
- 按天分桶用本地时区，跨时区后历史记录仍按当初的本地日期归桶
- 只统计 role 为 assistant 的记录，用户消息不计费
- reasoning 思考 token 不单独累加，它已含在 output 里
- 费用是估算，以你填的单价为准，与服务商实际账单可能有出入