# Sharp Agent

An AI agent for Windows. You describe a task in the chat, and the agent does it on your PC:
reads and edits files, searches folders, runs commands. No administrator rights needed.

The interface is in Russian.

## Features

- Models via [Tooken Club](https://tooken.club), your ChatGPT subscription (official "Sign in with ChatGPT")
  or [GroqCloud](https://console.groq.com).
- Reads and edits files, including Word, Excel and PowerPoint; searches by file name and content.
- Runs PowerShell and cmd commands; changes made by the agent can be undone.
- Plugins for Word, Excel, PDF, images, audio and video, data and charts, code projects.
  Some of them need external tools such as FFmpeg or Tesseract.
- Image generation, voice input, optional subagents for parallel analysis.
- Attachments by drag and drop or from the clipboard, file previews and diffs right in the window.
- Can ask before deleting, overwriting or running commands; deletes to the Recycle Bin; has a sandbox mode.
- Portable builds keep settings, chats and logs in the program folder. The Windows installer stores user data in `%LOCALAPPDATA%\SharpAgent`.

## Run

Requires Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/Flugih/sharp_agent.git
cd sharp_agent
dotnet run --project SharpAgent.csproj
```

Or open `SharpAgent.sln` in Visual Studio 2022 and press F5. In this mode the project folder is used for data when running from `bin`; a standalone portable build uses its executable folder.

On first launch the settings open: pick a provider, paste its API key or sign in with ChatGPT, and save.

## Portable build

```powershell
dotnet publish SharpAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Copy the `publish` folder to any Windows 10/11 x64 PC and run `SharpAgent.exe`. No .NET install needed.


## Windows installer

The **Windows installer** GitHub Actions workflow can be run manually from the Actions tab. Download `SharpAgent-Windows-Setup` from the workflow artifacts. Pushing a `v*` tag also creates or updates a GitHub Release and attaches `SharpAgent-Setup.exe`. The installer is self-contained for Windows x64 and does not require .NET to be installed.

Installation is per user, without administrator rights. Application files go to `%LOCALAPPDATA%\Programs\SharpAgent` by default; settings, credentials, history, workspace, temporary files and plugins go to `%LOCALAPPDATA%\SharpAgent`. Uninstalling leaves user data intact. Every upgrade replaces the files of the built-in plugins, so edits to them are lost and a deleted built-in plugin comes back; to keep a changed copy, put it in a new folder and change `name` in its `PLUGIN.md`. Your own plugins are never touched. The installed and portable versions use separate data; migration is manual.

To build the installer locally, install Inno Setup 6, run the portable `dotnet publish` command above into a clean `publish` folder (never one you have run the app from: it holds your keys and chats), then run:

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\SharpAgent.iss
```

The installer will be created in `artifacts`.
