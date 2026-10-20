# Semiconductor Data Analyzer

**CSV Analyzer V2.0.4 · GYF**

A Windows desktop application for semiconductor CSV / STDF test-data analysis, built with .NET 8 and WPF.

面向半导体测试数据的本地分析工具，支持 CSV、STDF V4、晶圆 Map、良率统计、阈值模拟和 Excel 报告。分析在本机进行，使用软件不需要上传测试文件。

## 产品数据管理入口（当前源码）

启动后先进入产品管理界面，也可一键打开原有独立分析器。创建产品时定义 CP / FT Testflow、温度、必测步骤、HBIN 良品归类和 FT UUID Test Num；登记散落在不同目录的数据文件并按产品、批次、晶圆管理，原文件不复制。

- CP 按批次 + 晶圆 + XY、FT 按批次 + eFuse / OTP UUID 匹配跨温度芯片；分温度良率和综合良率分别展示。
- 综合以必测步骤芯片的并集为分母，全部通过才算良品；缺温、缺 HBIN 与身份缺失明确区分，不相乘各阶段良率。
- 支持自动解析计算，或指定物理分 Bin 的良率（总数＋良品数，或只有百分比）；可独立登记物理测得的综合良率。只有各温度汇总率时不推算联合良率，缺少样本数时不虚构加权合计。
- 提供晶圆 / 批次 / 月 / 年四种口径、表头关键字与右键勾选筛选、良率与分类趋势、源文件重新定位和后台重算；支持管理报告 XLSX 与 PNG 导出。单温度步骤的良率明细隐藏重复综合行。
- 导入可按文件名字符位置识别片号、自动或指定格式识别测试时间，并可手动修改；未识别时间默认当前系统时间，支持统一修改全部时间。规则按产品记忆。数据文件支持多选同一步骤后自动合并打开分析器，保留各自配置与晶圆号。
- STDF / 压缩 STDF 登记时优先自动填入 MIR Lot、WIR / WRR 晶圆号及测试时间；多晶圆保留各自时间供年月统计，支持手动覆盖。基本信息在后台扫描，不建立测量矩阵。
- 支持删除产品与多条数据登记，仅删除本机目录记录及统计，所有原文件保留。
- 产品目录和统计摘要存于本机用户目录 `SemiconductorCsvAnalyzer/ProductLibrary`；保存时保留上一份备份，不保存完整测量矩阵。

只启动独立分析器可使用 `SemiconductorCsvAnalyzer.exe --analyzer`。详细口径见 [使用手册](Docs/UserGuide.md)。

## V2.0.4 更新

当前源码还包含一轮稳定性整理：原文快照按使用者释放，减少强制 GC；导入、重算与导出支持取消，关闭时先安全停止；默认配置迁移至当前用户目录并采用临时文件替换和 `.bak` 备份。界面说明集中到内置使用手册，手册只保留阅读和目录导航。产品数据库位置及格式保持兼容。

- **CP / FT**：默认 CP；FT 按指定的一个或多个 Test Num 读取 eFuse / OTP 原文，按顺序拼接 UUID 识别复测，支持预览与 INI 保存。UUID 缺失的记录独立保留并提示，原始测量值及 Run 序列号不改写。部分复测导出的整合 CSV 同时保存派生身份。
- **图表性能**：直方图直接绘制清晰的柱形与坐标；散点合成一个可复用的点图层，游标独立更新，不再额外缓存整张图。拉伸和缩放按真实数据重绘，切换测项保留旧图直至最新结果就绪，避免闪白。
- **主表与散点**：Fails、Max、Min；粉红色超限 Value、良率热力底色；Combine Site / Wafer；按 SN、Site、Wafer 排序；组间交替蓝色、组名与分隔线；可锁定纵轴对比测项。
- **BIN Map**：图例单项勾选或点击列头全选 / 取消；未勾选以灰色显示，统计不变。仅 SBIN 1 / HBIN 1 使用绿色，其他 BIN 的 50 色配色避开灰色和绿色系，报告颜色一致。
- **热力图与 Run**：自定义蓝 / 绿 / 红色标，多晶圆统一范围；Run 可按序列号或晶圆 + X/Y 定位。

当前源码版本为 V2.0.4；已发布的运行包版本以 Releases 页面为准。详细操作和统计口径见 [使用手册](Docs/UserGuide.md)。
## 功能

- **CSV / STDF 导入**：首次即可多选文件，并选择晶圆关系、测项匹配及限值来源；设置中可按按钮手动识别 CSV 格式、预览 / 编辑并另存 INI，支持多行名称、重复表头和引号内换行，旧配置兼容。读取 STDF V4 的 PTR、MPR、FTR，支持大小端及 `.std.gz` / `.stdf.gz`；93k 模式优先使用 TSR 的完整测项名称。详见 [CSV 格式说明](Docs/CSVFormats.md)。
- **STDF Lot Info**：在分析器顶部查看 MIR / MRR 批次、产品、程序、机台、温度与时间，以及 WIR / WRR 晶圆信息；左侧切换当前打开或追加的 STDF，支持复制全部信息。只在后台读取基本信息，不重算主表或绘图。
- **主列表统计**：良率、均值、中位数、总体 σ、Cpk、robust 统计、异常值数、Site 良率差异；支持 Combine Site、筛选、排序和关注项。
- **Run / Value**：按序列号浏览原始记录；缺失或重复序列号自动编号，保留复测身份；超限 Value 标红，良率渐变着色。
- **图表与 Map**：直方图、散点图、SBIN / HBIN / P/F Map；支持多晶圆、均值中心热力图、Site 边框和 BIN 数字。
- **BIN / Wafer Table**：自定义 SBIN 分类，按 Site、Wafer 比较数量及占比；分类趋势折线图附带数据表，可勾选需要显示的分类。
- **复测与整合**：首测与复测均保留、仅首测、复测覆盖首测、只保留复测；只保留复测按芯片和测项去掉首次，保留后续全部复测。支持多文件批量追加、调整导入顺序，并选择测项匹配方式及限值来源。整批成功后统一刷新，失败时保留当前数据。
- **阈值模拟与良率预测**：确认有效测项、调整上下限或关闭测项，联合判断整颗芯片；缺测预测区分确认结果、估计结果与无法估计。
- **导出**：整合 CSV、可选离群值剔除、四工作表 XLSX 报告及关注项图表。使用 ClosedXML，不依赖安装 Excel。

当前数据和配置名显示在窗口标题，三个 Map 统一使用晶圆图标。

详细操作与统计口径见 [使用手册](Docs/UserGuide.md)，也可在程序“关于 → 详细说明”中离线阅读。

## 下载运行

从 [GitHub Releases](https://github.com/ttt3t/semiconductor-data-analyzer/releases/latest) 下载 Windows x64 ZIP，解压后双击其中的 EXE。程序为自包含单文件版本，无需安装 .NET 或 Excel。CSV 导入前请在“设置”加载或调整布局配置；STDF 可直接打开。

## 开发与运行

本地提交、历史比较和 GitHub 同步方式见 [版本管理说明](Docs/VersionControl.md)。

需要 **Windows** 和 **.NET 8 SDK**。可用支持 .NET 8 的 Visual Studio 打开 `SemiconductorCsvAnalyzer.csproj`，或在仓库根目录执行：

```powershell
dotnet restore SemiconductorCsvAnalyzer.csproj
dotnet build SemiconductorCsvAnalyzer.csproj -c Release
dotnet run --project SemiconductorCsvAnalyzer.csproj -c Release
```

核心数据回归检查可在安装 .NET 8 SDK 的 Linux 或 Bash 环境运行：

```bash
bash Tests/run-core-memory.sh
```

该检查覆盖部分复测、共享列筛选、精度与错误值、原文租约、CSV / 整合 CSV / 大小端与压缩 STDF 的产品统计读取，以及跨温度良率和失败回滚。输出位于忽略的 `bin/CoreMemoryProbe`；它不替代 Windows 上的 WPF 界面回归。

筛选和复测结果共享基础数值列及行映射，编辑列时才复制；产品管理的良率重算仅保留身份、Bin、时间和必要的 UUID 原文，不创建完整数值列或测量原文快照。文件完整性校验及部分复测口径仍沿用原解析流程。

发布为 Windows x64 自包含单文件程序：

```powershell
dotnet publish SemiconductorCsvAnalyzer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/win-x64
```

将发布目录中的 `SemiconductorCsvAnalyzer.exe` 压缩为 ZIP 即可分发，PDB 无需附带；示例 `config.ini` 可按需要单独使用。首次运行后可在设置中定义并保存自己的配置。WPF 界面仅支持 Windows。

## CSV 示例配置

仓库的 `config.ini` 是通用示例，不对应任何生产数据：

- 前七列依次为 `SN, X, Y, Site, SBIN, HBIN, Wafer`，第八列开始为测试项。
- 第 1～7 行分别为测项名称、LSL、USL、单位、测试序号、测项 SBIN、测项 HBIN；第 8 行起为测量数据。
- 实际使用前，请在设置中按文件布局调整行列号。行列从 1 起，可选列用 0 表示未配置。
- STDF 使用文件自身结构，不需要 CSV 行列配置；HBIN 良品归类仍使用 INI。

SBIN 分类示例：

```ini
[SBinCategories]
坏点=1,2,3
坏线=4,5,6
```

## 实现与支持范围

数值使用按记录行号对齐的列式数组，身份信息由记录表共享；原始测量文本使用本机临时缓存。主表保留虚拟化，分析与绘图准备使用后台计算。

STDF 支持范围以手册为准；不支持的版本或部分记录会明确报错。不同测试程序的字段约定可能不同。良率预测不是确定判定，软件会标明参考不足、外推和覆盖情况。

## 反馈与贡献

欢迎提交 Issue 或 Pull Request。复现问题时请提供软件版本、操作步骤和经过脱敏的最小样例；不要提交生产测试文件、客户信息或凭据。本仓库不包含公司测试数据、本机诊断产物或个人配置。

## License

[MIT](LICENSE) · Copyright (c) 2026 **GYF**.

第三方依赖保留各自许可证；本项目的 MIT 许可证不替代其许可证。

