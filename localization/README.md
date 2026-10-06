# 界面语言 / Interface language

Two languages, switchable in 设置 → 常规 → 界面 → 界面语言. Chinese is the neutral set; English is a satellite
assembly. A missing English string falls back to Chinese on purpose — this application was written in Chinese,
so a gap degrades to the text that has always been there rather than to a language the user did not choose.

## The files

| Path | What it is |
|---|---|
| `localization/*.tsv` | **The source of truth.** One row per line: `key <TAB> 简体中文 <TAB> English`. |
| `src/FengSync/Localization/Strings.resx` | Generated. Neutral (Chinese). Do not edit. |
| `src/FengSync/Localization/Strings.en.resx` | Generated. English. Do not edit. |
| `src/FengSync/Localization/Tr.cs` | `Tr.F("Key")` and `Tr.Format("Key", args)`. |
| `src/FengSync/Localization/AppLanguageContext.cs` | The live choice, and the object every XAML binding reads. |
| `src/FengSync/Localization/LangExtension.cs` | The `{l:Lang}` markup extension that hands that object to a binding. |

Regenerate after editing a table:

```
python scripts/New-StringsResx.py localization src/FengSync/Localization/Strings.resx
```

A duplicate key or a missing translation is an error there, not something to notice later. The tables are kept
per area so one area can be re-translated without touching the other twenty.

## How a string is written

C#:

```csharp
Text = Tr.F("Mgr.NewEndpoint");                       // a plain string
Status.Text = Tr.Format("Main.StatusComparing", n);   // holes go in the table as {0}, {1}
```

XAML — the root element needs `xmlns:l="clr-namespace:FengSync.Localization"` and nothing else:

```xml
<Button Content="{Binding [Mgr.NewEndpoint], Source={l:Lang}}"/>
```

The source is the one long-lived `AppLanguageContext.Current`. It raises `PropertyChanged` with an **empty**
property name, which WPF reads as "every property changed", so the indexer bindings re-evaluate and a window
already on screen follows a language change without being rebuilt.

## Naming

`Area.Name` — `Mgr.`, `Main.`, `Settings.`, `Guide.`, `Cloud.`, `Mount.`, `Sftp.`, `Transfer.`, `Endpoint.`,
`Profile.`, `Sync.`, `Run.`, `About.`, `Update.`, `Schedule.`, `History.`, `Preview.`, `Conflict.`, `Bookmark.`,
`Recycle.`, `OAuth.`, `Thumb.`, `Service.`, `Task.`, `Rclone.`, `Mount.` …

A key is an identifier, not a sentence. `Mgr.Browse` and `Mgr.BrowseLocal` are two buttons; a key that reads
`Mgr.BrowseTheThingInTheBrowser` is a key nobody can find again.

## The rules that are not obvious

- **The Chinese column is byte-identical to the literal it replaced.** The ~600 UI tests assert today's wording;
  a well-meaning reword breaks all of them and buys nothing.
- **An AutomationId, an `x:Name`, an event handler name and a resource key are never translated.** They are
  identifiers, and the desktop scenarios drive the interface through them.
- **A Chinese literal that is compared, switched on or used as a dictionary key is an identifier too.** Give it
  a stable ASCII id and translate the label separately. `SettingsWindow`'s page names are the worked example.
- **Never a key:** file paths, rclone flags, URLs, `AutomationId`s, date and number format strings
  (`yyyy-MM-dd HH:mm:ss`), and anything a machine reads back.
- **A `const string` that is used as a `case` label or an attribute argument cannot become a method.** Leave it
  and say so in the report.
- `Tests/FengSync.Tests/LanguageCoverageTests.cs` fails the build on a missing key, an orphan key, a hand-edited
  `.resx`, and any Chinese literal left in a string in the shell. Comments are exempt — they are documentation
  and stay in the language they were written in.

## Scope

`src/FengSync/` — the WPF shell. `src/FengSync.Core/` still carries Chinese in its own messages (about 325 lines
across 54 files); those reach the user through a shell string. Translating them is a separate job with its own
resource set, and until it is done an English interface will show a Chinese sentence wherever a Core message is
surfaced on its own.
