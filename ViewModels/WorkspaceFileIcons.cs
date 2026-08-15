using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;

namespace Athena.UI.ViewModels;

/// <summary>
/// 把工作区文件名映射到图标契约 key（Styles/AppIcons.axaml 中的 AthenaIcon* 名称）。
///
/// 只按"这个文件是什么"分类，不按具体格式细分：CoreUI 没有品牌化的文件类型图标，
/// 硬凑一一对应只会得到一堆看起来一样的方块。归不了类的一律落到通用文件图标。
/// </summary>
public static class WorkspaceFileIcons
{
    public const string Generic = "AthenaIconFileGeneric";

    private static readonly FrozenDictionary<string, string> ByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // 源码
        [".cs"] = "AthenaIconFileCode", [".fs"] = "AthenaIconFileCode", [".vb"] = "AthenaIconFileCode",
        [".ts"] = "AthenaIconFileCode", [".tsx"] = "AthenaIconFileCode", [".js"] = "AthenaIconFileCode",
        [".jsx"] = "AthenaIconFileCode", [".mjs"] = "AthenaIconFileCode", [".cjs"] = "AthenaIconFileCode",
        [".py"] = "AthenaIconFileCode", [".rb"] = "AthenaIconFileCode", [".go"] = "AthenaIconFileCode",
        [".rs"] = "AthenaIconFileCode", [".java"] = "AthenaIconFileCode", [".kt"] = "AthenaIconFileCode",
        [".swift"] = "AthenaIconFileCode", [".c"] = "AthenaIconFileCode", [".h"] = "AthenaIconFileCode",
        [".cpp"] = "AthenaIconFileCode", [".hpp"] = "AthenaIconFileCode", [".cc"] = "AthenaIconFileCode",
        [".php"] = "AthenaIconFileCode", [".lua"] = "AthenaIconFileCode", [".dart"] = "AthenaIconFileCode",
        [".scala"] = "AthenaIconFileCode", [".r"] = "AthenaIconFileCode", [".ipynb"] = "AthenaIconFileCode",

        // 标记与样式（结构化界面文件，和纯文本区分开）
        [".xaml"] = "AthenaIconFileCode", [".axaml"] = "AthenaIconFileCode", [".xml"] = "AthenaIconFileCode",
        [".xsd"] = "AthenaIconFileCode", [".css"] = "AthenaIconFileCode", [".scss"] = "AthenaIconFileCode",
        [".less"] = "AthenaIconFileCode", [".vue"] = "AthenaIconFileCode", [".svelte"] = "AthenaIconFileCode",

        // 网页
        [".html"] = "AthenaIconFileWeb", [".htm"] = "AthenaIconFileWeb", [".xhtml"] = "AthenaIconFileWeb",

        // 脚本
        [".sh"] = "AthenaIconFileScript", [".bash"] = "AthenaIconFileScript", [".zsh"] = "AthenaIconFileScript",
        [".fish"] = "AthenaIconFileScript", [".ps1"] = "AthenaIconFileScript", [".psm1"] = "AthenaIconFileScript",
        [".bat"] = "AthenaIconFileScript", [".cmd"] = "AthenaIconFileScript",

        // 配置
        [".json"] = "AthenaIconFileConfig", [".jsonc"] = "AthenaIconFileConfig", [".json5"] = "AthenaIconFileConfig",
        [".yaml"] = "AthenaIconFileConfig", [".yml"] = "AthenaIconFileConfig", [".toml"] = "AthenaIconFileConfig",
        [".ini"] = "AthenaIconFileConfig", [".cfg"] = "AthenaIconFileConfig", [".conf"] = "AthenaIconFileConfig",
        [".env"] = "AthenaIconFileConfig", [".properties"] = "AthenaIconFileConfig",
        [".csproj"] = "AthenaIconFileConfig", [".fsproj"] = "AthenaIconFileConfig", [".sln"] = "AthenaIconFileConfig",
        [".props"] = "AthenaIconFileConfig", [".targets"] = "AthenaIconFileConfig",

        // 纯文本 / 文档
        [".md"] = "AthenaIconFileText", [".markdown"] = "AthenaIconFileText", [".mdx"] = "AthenaIconFileText",
        [".txt"] = "AthenaIconFileText", [".rst"] = "AthenaIconFileText", [".log"] = "AthenaIconFileText",
        [".doc"] = "AthenaIconFileDocument", [".docx"] = "AthenaIconFileDocument", [".dotx"] = "AthenaIconFileDocument",
        [".rtf"] = "AthenaIconFileDocument", [".odt"] = "AthenaIconFileDocument", [".pdf"] = "AthenaIconFileDocument",
        [".tex"] = "AthenaIconFileDocument",

        // 表格与幻灯片
        [".xls"] = "AthenaIconFileSpreadsheet", [".xlsx"] = "AthenaIconFileSpreadsheet",
        [".xlsm"] = "AthenaIconFileSpreadsheet", [".xltx"] = "AthenaIconFileSpreadsheet",
        [".ods"] = "AthenaIconFileSpreadsheet", [".csv"] = "AthenaIconFileSpreadsheet",
        [".tsv"] = "AthenaIconFileSpreadsheet",
        [".ppt"] = "AthenaIconFilePresentation", [".pptx"] = "AthenaIconFilePresentation",
        [".potx"] = "AthenaIconFilePresentation", [".odp"] = "AthenaIconFilePresentation",
        [".key"] = "AthenaIconFilePresentation",

        // 图像 / 音视频
        [".png"] = "AthenaIconFileImage", [".jpg"] = "AthenaIconFileImage", [".jpeg"] = "AthenaIconFileImage",
        [".gif"] = "AthenaIconFileImage", [".bmp"] = "AthenaIconFileImage", [".webp"] = "AthenaIconFileImage",
        [".svg"] = "AthenaIconFileImage", [".ico"] = "AthenaIconFileImage", [".icns"] = "AthenaIconFileImage",
        [".tif"] = "AthenaIconFileImage", [".tiff"] = "AthenaIconFileImage", [".heic"] = "AthenaIconFileImage",
        [".mp4"] = "AthenaIconFileVideo", [".mov"] = "AthenaIconFileVideo", [".mkv"] = "AthenaIconFileVideo",
        [".avi"] = "AthenaIconFileVideo", [".webm"] = "AthenaIconFileVideo", [".m4v"] = "AthenaIconFileVideo",
        [".mp3"] = "AthenaIconFileAudio", [".wav"] = "AthenaIconFileAudio", [".flac"] = "AthenaIconFileAudio",
        [".m4a"] = "AthenaIconFileAudio", [".ogg"] = "AthenaIconFileAudio", [".aac"] = "AthenaIconFileAudio",

        // 归档 / 二进制 / 数据库 / 字体 / 密钥
        [".zip"] = "AthenaIconFileArchive", [".tar"] = "AthenaIconFileArchive", [".gz"] = "AthenaIconFileArchive",
        [".tgz"] = "AthenaIconFileArchive", [".bz2"] = "AthenaIconFileArchive", [".xz"] = "AthenaIconFileArchive",
        [".7z"] = "AthenaIconFileArchive", [".rar"] = "AthenaIconFileArchive", [".nupkg"] = "AthenaIconFileArchive",
        [".exe"] = "AthenaIconFileBinary", [".dll"] = "AthenaIconFileBinary", [".so"] = "AthenaIconFileBinary",
        [".dylib"] = "AthenaIconFileBinary", [".bin"] = "AthenaIconFileBinary", [".wasm"] = "AthenaIconFileBinary",
        [".o"] = "AthenaIconFileBinary", [".a"] = "AthenaIconFileBinary",
        [".db"] = "AthenaIconFileDatabase", [".sqlite"] = "AthenaIconFileDatabase",
        [".sqlite3"] = "AthenaIconFileDatabase", [".sql"] = "AthenaIconFileDatabase",
        [".ttf"] = "AthenaIconFileFont", [".otf"] = "AthenaIconFileFont", [".woff"] = "AthenaIconFileFont",
        [".woff2"] = "AthenaIconFileFont", [".eot"] = "AthenaIconFileFont",
        [".pem"] = "AthenaIconFileSecret", [".crt"] = "AthenaIconFileSecret", [".cer"] = "AthenaIconFileSecret",
        [".p12"] = "AthenaIconFileSecret", [".pfx"] = "AthenaIconFileSecret", [".keystore"] = "AthenaIconFileSecret",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 无扩展名但含义明确的文件；这些名字在仓库根目录出现得比任何扩展名都频繁。
    /// </summary>
    private static readonly FrozenDictionary<string, string> ByFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".gitignore"] = "AthenaIconFileVcs", [".gitattributes"] = "AthenaIconFileVcs",
        [".gitmodules"] = "AthenaIconFileVcs", [".gitkeep"] = "AthenaIconFileVcs",
        ["dockerfile"] = "AthenaIconFileScript", ["makefile"] = "AthenaIconFileScript",
        ["license"] = "AthenaIconFileDocument", ["readme"] = "AthenaIconFileText",
        [".editorconfig"] = "AthenaIconFileConfig", [".npmrc"] = "AthenaIconFileConfig",
        [".dockerignore"] = "AthenaIconFileConfig",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string ForFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Generic;
        }

        if (ByFileName.TryGetValue(fileName, out var byName))
        {
            return byName;
        }

        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(extension) && ByExtension.TryGetValue(extension, out var byExtension))
        {
            return byExtension;
        }

        // .tar.gz / .tar.bz2 之类的双扩展名，Path.GetExtension 只会给出后半段，
        // 上面的表已经覆盖；这里只兜住 "README"（无扩展名）这类去掉后缀后仍可识别的名字。
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return ByFileName.TryGetValue(stem, out var byStem) ? byStem : Generic;
    }
}
