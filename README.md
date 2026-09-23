# Semiconductor Data Analyzer

**CSV Analyzer V2.0 · GYF**

A Windows desktop application for semiconductor CSV / STDF test-data analysis, built with .NET 8 and WPF.

面向半导体测试数据的本地分析工具，支持 CSV、STDF V4、晶圆 Map、良率统计、阈值模拟和 Excel 报告。分析在本机进行，使用软件不需要上传测试文件。

## 功能

- **CSV / STDF 导入**：INI 配置 CSV 布局；读取 STDF V4 的 PTR、MPR、FTR，支持大小端及 `.std.gz` / `.stdf.gz`；93k 模式优先使用 TSR 的完整测项名称。
- **主列表统计**：良率、均值、中位数、总体 σ、Cpk、robust 统计、异常值数、Site 良率差异；支持 Combine Site、筛选、排序和关注项。
- **Run / Value**：按序列号浏览原始记录；缺失或重复序列号自动编号，保留复测身份；超限 Value 标红，良率渐变着色。
- **图表与 Map**：直方图、散点图、SBIN / HBIN / P/F Map；支持多晶圆、均值中心热力图、Site 边框和 BIN 数字。
- **BIN / Wafer Table**：自定义 SBIN 分类，按 Site、Wafer 比较数量及占比。
- **复测与整合**：首测与复测均保留、仅首测、复测覆盖首测；可追加数据并选择测项匹配方式及限值来源。
- **阈值模拟与良率预测**：确认有效测项、调整上下限或关闭测项，联合判断整颗芯片；缺测预测区分确认结果、估计结果与无法估计。
- **导出**：整合 CSV、可选离群值剔除、四工作表 XLSX 报告及关注项图表。使用 ClosedXML，不依赖安装 Excel。

详细操作与统计口径见 [使用手册](Docs/UserGuide.md)，也可在程序“关于 → 详细说明”中离线阅读。

## 开发与运行

需要 **Windows** 和 **.NET 8 SDK**。可用支持 .NET 8 的 Visual Studio 打开 `SemiconductorCsvAnalyzer.csproj`，或在仓库根目录执行：

```powershell
dotnet restore SemiconductorCsvAnalyzer.csproj
dotnet build SemiconductorCsvAnalyzer.csproj -c Release
dotnet run --project SemiconductorCsvAnalyzer.csproj -c Release
```

发布为 Windows x64 自包含程序：

```powershell
dotnet publish SemiconductorCsvAnalyzer.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

发布目录包含程序及运行所需文件。WPF 界面仅支持 Windows。

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
