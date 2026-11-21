# Samples

## Pdfwright.Samples.Report

A multi-page report made only with the public API of Pdfwright: a heading, a row of KPIs in a callout, a bar chart drawn on the canvas, a table of 90 invoices that paginates with its header repeated, a
header (from the second page) and a footer reading "Page x of y", and a section in Arabic with Latin words in it, with a small right-to-left table. The code is in
[`ReportSample.cs`](Pdfwright.Samples.Report/ReportSample.cs); `Program.cs` loads the fonts, saves the document to a file and prints the page count and any missing glyph.

```bash
dotnet run --project samples/Pdfwright.Samples.Report -- report.pdf
```

Open `report.pdf` in a viewer. The sample is part of `Pdfwright.slnx`, so CI builds it with everything else, and the test project compiles `ReportSample.cs` and builds, saves and reads back the same report
(`ReportSampleTests`), so the sample is known to work on every target framework.

**Fonts.** The sample uses Noto Sans (regular and bold) and Noto Sans Arabic from the test project's `Fonts` folder, linked into the project and copied beside the program; the repository holds one copy of each. They are
SIL Open Font License 1.1 fonts, unmodified and not renamed, and the licence texts are copied into `fonts/` beside them (and listed in [NOTICE](../NOTICE)). A program of your own supplies its own fonts: Pdfwright never
looks in the operating system.

The logo in the header is the small test image `tests/Pdfwright.Tests/Images/Files/gradient-24x16.jpg`.
