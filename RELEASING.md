# Releasing

> **Status: version 0.1 is complete and nothing is published.** `Pdfwright` is not on nuget.org, there is no Trusted Publishing policy yet, and no `v*` tag exists. Everything under
> "Before 0.1.0, by hand" and "The first release" is the maintainer's (not an agent's, not CI's). Do not push a `v*` tag until the policy and the `NUGET_USER` secret exist and the checks below are done.

A release is a tag. `.github/workflows/release.yml` reads the version from the
tag name, so there is no version in a file to forget to bump and no way for the
tag and the published package to disagree. (`VersionPrefix` in `Directory.Build.props` is what a local
`dotnet pack` produces, and is deliberately not a release version.)

```bash
git tag v0.1.0
git push origin v0.1.0
```

The workflow checks that the tagged commit is on `main`, builds, runs the tests on all four target frameworks (net8.0 to net11.0), packs,
publishes to nuget.org and creates the GitHub release with the `.nupkg` and
`.snupkg` attached.

## Publishing uses Trusted Publishing, not an API key

nuget.org issues a short-lived key in exchange for a GitHub OIDC token, so this
repository stores no publishing credential at all. There is nothing to rotate
and nothing to leak.

The setup is one policy on nuget.org and one repository secret here.

### The policy, on nuget.org

Sign in, then **your username → Trusted Publishing → add a policy**:

| Field | Value |
|---|---|
| Policy Name | `pdfwright-release` — a label for your own list; nothing matches against it |
| Repository Owner | `sinanoran` |
| Repository | `pdfwright` |
| Workflow File | `release.yml` — the file name only, not the path |
| Environment | leave empty; this workflow uses no GitHub environment |

Under **Scopes**, allow publishing **new packages** as well as new versions, with
the glob `Pdfwright*`. Version 0.1.0 is the first push of an id that does not
exist on nuget.org yet, and a policy scoped to existing packages only will
refuse it.

A policy on a public repository is active immediately. (On a private one it
starts as *pending full activation* for seven days, because nuget.org needs the
repository and owner ids from a real publish to pin the policy against somebody
deleting the repository and recreating it under the same name.)

### The secret, here

`NuGet/login@v1` needs the nuget.org **profile name** — not the email address —
to know whose policy to match:

```bash
gh secret set NUGET_USER --repo sinanoran/pdfwright
```

It is not really a secret, but nuget.org's own documentation recommends passing
it as one, and there is no cost to doing so.

## Before 0.1.0, by hand

These are the checks no test stands in for; the tests pass without them. Note what you found (a line in the pull request or the release notes is enough), and send corrections to the corpus as a diff of the data file.

1. **Native-speaker review of the corpus text.** `tests/Pdfwright.Tests/Corpus/pages/*.txt` and the single lines in `Corpus/*.txt` are original, ordinary phrases, but a test cannot tell whether a sentence is
   natural or a word is spelled the way a reader expects. The sample report and the user guide also carry a few Arabic sentences (`samples/Pdfwright.Samples.Report/ReportSample.cs`, `docs/guide.md`). Have a speaker of each
   language read its lines in a viewer, not in the source (a few minutes each): **Arabic, Hebrew, Hindi (Devanagari), Bengali, Tamil and Thai**, and for the pages in Latin and Cyrillic script
   **Turkish, German, Polish, Vietnamese, Greek and Russian**. Two items of the Arabic reviewer's list were flagged and left as written: `ReportSample.cs` calls Latin codes (Q3 and INV-2026-0042) "الأرقام الإنجليزية"
   (the English numbers), which may not be the usual way to say it, and `docs/guide.md` shows the footer "⁦1 / 3⁩ صفحة" where the usual form may be "صفحة 1 من 3".
2. **Open the sample files in the viewers people use:** Chrome, Edge, Adobe Acrobat Reader and macOS Preview. Make the files:

   ```bash
   dotnet build Pdfwright.slnx
   PDFWRIGHT_SAMPLES_DIR=/tmp/pdfwright-samples tests/Pdfwright.Tests/bin/Debug/net10.0/Pdfwright.Tests -class "*SampleDocumentTests"   # the README report, a right-to-left report, a page of pictures
   PDFWRIGHT_DUMP=/tmp/pdfwright-corpus tests/Pdfwright.Tests/bin/Debug/net10.0/Pdfwright.Tests -class "*ScriptConformanceTests"          # the twenty corpus documents: ten scripts, a short and a long page each
   dotnet run --project samples/Pdfwright.Samples.Report -- /tmp/pdfwright-samples/report-sample.pdf                                        # the report of the samples folder
   ```

   Look, in each viewer, for: Arabic letters joined and in the right order; Hebrew points and Arabic marks on their letters; Devanagari conjuncts and reordered vowel signs; Bengali and Tamil vowel signs; Thai stacked marks;
   mixed-direction lines in the order the Unicode algorithm gives; the table's header on every page; "Page x of y" right in every footer; nothing outside the margins; the bar chart. Then select a line, copy it and paste it:
   it gives the text (an invisible U+034F in Arabic, Indic and Thai text is expected, see `docs/known-limitations.md`). If one viewer shows something wrong, that is a defect to fix before 0.1.0, or a line in the known limitations.
3. **Run the pdf.js check** on both sets, the corpus dump and the dump of the 61 mixed-direction lines, and record each pass count and the pdf.js version in `docs/plan-0.1.md` (the corpus was 3120 of 3120 in
   Firefox 157.0's pdf.js on 21 November 2026, and the 61 lines were 61 of 61 in pdf.js 6.3.335 the same day; the script prints the version it measured, and compares in NFD with white space, U+034F and the
   bidirectional controls removed):

   ```bash
   node tools/pdfjs-check/pdfjs-check.js /tmp/pdfwright-corpus tests/Pdfwright.Tests/Corpus
   PDFWRIGHT_DUMP=/tmp/pdfwright-mixed tests/Pdfwright.Tests/bin/Debug/net10.0/Pdfwright.Tests -class "*MixedDirection*"
   node tools/pdfjs-check/pdfjs-check.js /tmp/pdfwright-mixed tests/Pdfwright.Tests/Corpus
   ```
4. **Read the documentation as a reader would.** Read the README and `docs/guide.md` once on GitHub (the code blocks are tested, the prose is not), and follow the links.
5. **Check the package.** `dotnet pack src/Pdfwright/Pdfwright.csproj --configuration Release --output artifacts`, then open the `.nupkg` (it is a zip): it holds the library for `net8.0`, `net9.0`, `net10.0` and `net11.0`,
   the XML documentation, `README.md`, `LICENSE` and `NOTICE`, and no font; its dependencies are `HarfBuzzSharp` and `HarfBuzzSharp.NativeAssets.Linux`.
6. **Decide that it is time.** Section 11 of the plan lists what was left open, and `docs/known-limitations.md` is what 0.1.0 will be judged by.

## The first release

1. Create the **Trusted Publishing policy** and the **`NUGET_USER` secret** above, and confirm on nuget.org that the id `Pdfwright` is still free (it was on 21 November 2026; nothing was reserved).
2. Make `CHANGELOG.md` say what 0.1.0 is: rename `[Unreleased]` to `[0.1.0] - <date>` (keep an empty `[Unreleased]` above it) and fix the link definitions at the bottom. Put the same summary, shortened, in a
   `PackageReleaseNotes` property of `src/Pdfwright/Pdfwright.csproj` if you want it beside the version on nuget.org (Sheetwright does).
3. Update the **status paragraphs** that say nothing is published: the top of `README.md` and of this file, the first line of `docs/known-limitations.md`, and "Where it stands" in `docs/plan-0.1.md`. Add the NuGet badge to the README
   (`https://img.shields.io/nuget/v/Pdfwright.svg`).
4. Merge that, and check **CI is green on `main`** for the commit being tagged.
5. Tag and push: `git tag v0.1.0 && git push origin v0.1.0`. Watch the **Release** workflow.
6. Check nuget.org (the package page and its README, **and that the links and images in the nuget.org README work**: the packed README has them made absolute at pack time, and only the published page proves it; the version, the dependencies; indexing takes a few minutes) and the GitHub release (the `.nupkg` and `.snupkg`). Install the package in a new console project and run the
   quick start of the README with your own fonts.

## Before tagging, every time

- `CHANGELOG.md` has a section for the version, and the link definitions at the
  bottom point at it.
- CI is green on `main` for the commit being tagged. The release workflow runs
  the tests again, but finding out at push time is worse than finding out before.

## If the push fails

- **`--skip-duplicate` swallowed it.** The push step passes this flag, so
  re-tagging an already-published version succeeds without republishing. Check
  nuget.org rather than the exit code. The release step is the same shape: it
  replaces the assets on a release that already exists rather than failing, so a
  tag moved onto a new commit re-runs cleanly.
- **The key expired.** It is valid for one hour and is requested in the step
  immediately before the push, so this means something in front of it took
  longer than an hour. Re-run the job.
- **The policy did not match.** The workflow file name, repository owner and
  repository in the policy are compared literally. Renaming this file means
  editing the policy.
- **The tagged commit is not on `main`.** The first step refuses it before anything is built; merge the commit and tag again.
