using Athena.UI.Models;
using System.Threading.Tasks;

namespace Athena.UI.Services.VirtualPet;

/// <summary>宠物养成存档的持久化端口。文件实现用于生产，内存实现用于设计器与测试。</summary>
public interface IPetProfileStore
{
    VirtualPetProfileDocument Load();

    Task SaveAsync(VirtualPetProfileDocument document);
}
