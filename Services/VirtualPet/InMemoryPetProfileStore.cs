using Athena.UI.Models;
using System.Threading.Tasks;

namespace Athena.UI.Services.VirtualPet;

/// <summary>
/// 不落盘的存档实现。设计器构造与测试用它，这样养成逻辑可以在没有 AthenaData
/// 目录的进程里完整跑起来，也不会污染用户的真实存档。
/// </summary>
public sealed class InMemoryPetProfileStore : IPetProfileStore
{
    private VirtualPetProfileDocument _document;

    public InMemoryPetProfileStore(VirtualPetProfileDocument? seed = null)
    {
        _document = seed ?? new VirtualPetProfileDocument();
    }

    /// <summary>已发生的写入次数。测试用它断言"关键事件立即落盘、普通事件走去抖"。</summary>
    public int SaveCount { get; private set; }

    public VirtualPetProfileDocument Load() => _document;

    public Task SaveAsync(VirtualPetProfileDocument document)
    {
        _document = document;
        SaveCount++;
        return Task.CompletedTask;
    }
}
