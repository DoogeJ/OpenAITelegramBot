@echo off
RMDIR "bin\Release" /S /Q
dotnet publish -c Release --runtime win-x64 --self-contained
del "bin\Release\net7.0\win-x64\publish\OpenAITelegramBot.pdb"
