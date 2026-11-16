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
- Portable: keeps settings, chats and logs in its own folder, nothing in `%APPDATA%` or the registry.

## Run

Requires Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/Flugih/sharp_agent.git
cd sharp_agent
dotnet run --project SharpAgent.csproj
```

Or open `SharpAgent.sln` in Visual Studio 2022 and press F5.

On first launch the settings open: pick a provider, paste its API key or sign in with ChatGPT, and save.

## Portable build

```powershell
dotnet publish SharpAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Copy the `publish` folder to any Windows 10/11 x64 PC and run `SharpAgent.exe`. No .NET install needed.
