# AGENTS.md

Rules for every change to Sharp Agent.

## Main rule: built-in tools only
- No NuGet packages, vendored DLLs or downloaded tools. Use the .NET 8 BCL and WPF, Win32/COM through P/Invoke, and what ships with Windows (PowerShell 5.1, cmd).
- Plugins may call optional tools the user installed (FFmpeg, Tesseract), but the app never downloads or bundles them.
- The app itself only starts the sing-box executable the user picked in the network settings and uses the installed WebView2 Runtime for the built-in browser; it never downloads or bundles them either.

## .NET 8
- Target `net8.0-windows`, C# 12. No .NET 9+ APIs (`System.Threading.Lock`, `Task.WhenEach`, ...) and no C# 13+ syntax (`field`, `\e`, partial properties).

## No code bloat
- Every line must earn its place: no dead code, unused members, usings or parameters, speculative options, forwarding wrappers or copies of existing helpers. Reuse or extend what exists: helpers, App.xaml styles, tool plumbing.
- Concise C# 12: file-scoped namespaces, primary constructors, expression bodies, switch expressions, pattern matching, collection expressions, target-typed `new()`, `var`, `??=`, `using var`.
- Omit what changes nothing: default `private`, `this.`, `else` after `return`, redundant casts and parentheses, blank lines inside methods.
- Compact but readable: one statement per line, meaningful member names; short names only for locals and lambdas.
- If a change adds more code than the feature needs, shrink it before finishing.

## Comments
- Only where the code cannot say it: one short line with the main idea (why, not what). No XML docs, banners, commented-out code or change notes.

## Keep stable
- UI texts are Russian. Keep UI strings, the system prompt, tool names, descriptions, JSON schemas and output prefixes unchanged unless the change is the point.
- Stored formats stay compatible: `.env` keys, `state.json`, `history/`, `auth/chatgpt.json`.
- Portable builds keep data in the program folder (`AppPaths`). Installed builds store user data in `%LOCALAPPDATA%\SharpAgent`; preserve both modes and their secret/app-file guards. Apart from that, never write to `%APPDATA%`, `%TEMP%` or the registry.
- Safety: every tool path goes through `ToolContext.Resolve` and the secret/app-file guards; risky actions ask when settings say so; sandbox mode disables commands, plugins and Computer Use; `.env`, `auth/` and keys never reach the model, logs or UI.

## Before finishing
- `dotnet build SharpAgent.csproj -o <folder outside bin\Debug>` with 0 warnings (the owner may be running `bin\Debug`).
