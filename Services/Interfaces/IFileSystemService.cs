using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 本地系统文件服务，提供面向本机文件系统的读写能力（受白名单保护）
/// </summary>
public interface IFileSystemService
{
    Task<string?> ReadFileAsync(string absolutePath);
    Task<bool> WriteFileAsync(string absolutePath, string content);
    Task<FileUpdateResult> ModifyFileWithDiffAsync(string absolutePath, string diffContent, bool fuzzyMatch = true);
    Task<bool> DeleteFileAsync(string absolutePath);
    Task<List<string>> ListDirectoryAsync(string absolutePath, bool recursive = false);
}
