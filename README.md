# DiscordFS
Use Discord as your personal free cloud storage

<img align="right" width="auto" height="300" src="https://user-images.githubusercontent.com/1809172/225082445-b201e99b-2eff-4426-ba00-7e3c633c36ad.png">

<br clear="left"/>

## Current State
**PROJECT IS WORK IN PROGRESS / NOT READY FOR USE YET**

ToDo before v1.0 release:  
- [x] Synchronize data from client to Discord
- [x] [Synchronize data from Discord to client](https://github.com/Trojaner/DiscordFS/issues/10)
- [x] Windows Explorer on-demand files integration
- [x] File placeholders
- [x] File index database
- [x] File serialization & chunking
- [x] File compression
- [x] [File encryption](https://github.com/Trojaner/DiscordFS/issues/1)
- [x] [Report sync download/upload progress to Windows](https://github.com/Trojaner/DiscordFS/issues/4)
- [x] [Read / write files using streams instead of byte arrays to reduce memory usage](https://github.com/Trojaner/DiscordFS/issues/5)
- [x] [ReadFileAsync / WriteFileAsync / GetFileListAsync Discord based implementation](https://github.com/Trojaner/DiscordFS/issues/7)
- [x] Fix [moving files](https://github.com/DiscordFS/DiscordFS/issues/20)
- [x] Fix [placeholders for directories](https://github.com/DiscordFS/DiscordFS/issues/21)
- [ ] Fix [random crashes](https://github.com/DiscordFS/DiscordFS/issues/11)
- [ ] Fix [renaming files](https://github.com/DiscordFS/DiscordFS/issues/15)
- [ ] [Encrypt index.db](https://github.com/DiscordFS/DiscordFS/issues/23)
- [ ] [Display current sync status in Windows Explorer](https://github.com/Trojaner/DiscordFS/issues/2)
- [ ] [Proper UI (initial set-up, sync status, etc.)](https://github.com/Trojaner/DiscordFS/issues/6)
- [ ] [Auto-run on system startup](https://github.com/Trojaner/DiscordFS/issues/8)

## Quick Start

### Requirements
- Windows 10 Fall Creators Update or newer
- .NET 7 Runtime ([x64](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-7.0.3-windows-x64-installer) | [x86](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-7.0.3-windows-x86-installer) | [arm64](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-7.0.3-windows-arm64-installer))

### Preparation
Before starting, you will have to create a Discord bot.  
This will take less than 5 minutes.  

#### Setting up the Discord bot  
  - First go to [Discord Applications Portal](https://discord.com/developers/applications/) and create a new application 
  - On the left, under `Settings`, click on `Bot` and add a bot:  
       ![image](https://user-images.githubusercontent.com/1809172/225075410-ab913e9c-3d74-4668-aea4-f89a058742ae.png)  
  - Copy the Bot token (you will need it later):  
       ![image](https://user-images.githubusercontent.com/1809172/225076797-fca82c90-dc52-483f-bc93-3e8290709a66.png)  
  - On the left, under `Settings`, click on `OAuth2` and copy the Client ID.  
    ![image](https://user-images.githubusercontent.com/1809172/225078262-539fec5b-9129-441f-96b8-3319e1c56fef.png)  
    ![image](https://user-images.githubusercontent.com/1809172/225078124-743ed2e6-df00-4aaa-a54f-53ef8d82bba0.png)  
  - Replace the Client ID in the link below and invite the bot to your server with it:  
    `https://discord.com/api/oauth2/authorize?client_id=YOUR_CLIENT_ID&permissions=8&scope=bot`

### Installation
Download the latest version from [Releases](https://github.com/Trojaner/DiscordFS/releases).  
After installing the package, the app will guide you through the setup process.  

Once the setup is finished, you should see a new "Discord.FS - [...]" tab on the left in the Windows Explorer.

## Disclaimer
THIS PROJECT IS NOT ENDORESED BY, AFFILIATED WITH, MAINTAINED, AUTHORIZED OR SPONSORED BY DISCORD INC OR ANY OF ITS SUBSIDIARIES OR ITS AFFILIATES.
THE USE OF ANY TRADE NAME OR TRADEMARK IS FOR IDENTIFICATION AND REFERENCE PURPOSES ONLY AND DOES NOT IMPLY ANY 
ASSOCIATION WITH THE TRADEMARK HOLDER OF THEIR PRODUCT BRAND.
  
DO NOT STORE ANY IMPORTANT FILES USING THIS SOFTWARE. 
DISCORD INC MAY REMOVE OR RESTRICT ACCESS TO ANY FILES AT ANY GIVEN TIME FOR ANY REASON AT ITS SOLE DISCRETION. 
STORED FILES MAY GET CORRUPTED OR LOST AT ANY GIVEN TIME FOR ANY REASON. 

NEITHER THE CREATORS OF THIS PROJECT NOR THE CONTRIBUTORS MAY BE HELD LIABLE FOR ANY DIRECT OR INDIRECT DAMAGE CAUSED BY 
THE USAGE OF THIS SOFTWARE IN ANY WAY.

## Copyright
Copyright (C) 2023 Enes Sadık Özbek and contributors.

Most of this project's code is, unless otherwise specified, licensed under the GNU GPL v3 license.  
See the [LICENSE](https://github.com/Trojaner/DiscordFS/blob/main/LICENSE) file for more information.

## Acknowledgements
- [cfapiSync](https://github.com/styletronix/cfapiSync) by [styletronix](https://github.com/styletronix) ([www.styletronix.net](www.styletronix.net)) for providing an example Windows CF API integration
- Projects such as [discord-fs by pixelomer](https://github.com/pixelomer/discord-fs) and [discord-fs by fr34kyn01535](https://github.com/fr34kyn01535/discord-fs) for inspiring the project idea
