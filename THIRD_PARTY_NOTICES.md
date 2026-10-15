# Third-party notices

Feng Sync release packages include third-party software. Their licenses apply
to those components independently of the Feng Sync MIT License.

## rclone

Release packages include `Assets/rclone/rclone.exe` from rclone.

Copyright (C) 2012 Nick Craig-Wood.

rclone is licensed under the MIT License:

> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in
> all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
> THE SOFTWARE.

Source and license: <https://github.com/rclone/rclone>

## NuGet dependencies

The project references the following direct packages. Release-package
maintainers must retain the license files supplied by each resolved NuGet
package, including transitive dependencies, when changing package versions.

| Package | Version | License/source |
| --- | --- | --- |
| Microsoft.Data.Sqlite | 10.0.0 | [MIT](https://licenses.nuget.org/MIT) |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.12 | [NuGet package metadata](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/2.1.12) |
| System.Management | 9.0.0 | [NuGet package metadata](https://www.nuget.org/packages/System.Management/9.0.0) |
| FluentIcons.Wpf | 2.1.333 | [NuGet package metadata](https://www.nuget.org/packages/FluentIcons.Wpf/2.1.333) |
| QRCoder | 1.8.1 | [MIT](https://licenses.nuget.org/MIT) |
| System.Drawing.Common | 6.0.0 | [MIT](https://licenses.nuget.org/MIT) — transitive dependency of QRCoder; it is resolved but never called, because the pairing picture comes from `PngByteQRCode`. |

The LAN quick-transfer listener is hosted by `Microsoft.AspNetCore.App` (Kestrel). A
self-contained release package therefore also carries that shared framework's
assemblies; they are part of .NET and licensed under MIT — see
<https://github.com/dotnet/aspnet.core>.
