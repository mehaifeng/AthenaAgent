using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Athena.UI.Models
{
    public partial class KnowledgeFileNode : ObservableObject
    {
        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private bool _isDirectory;

        [ObservableProperty]
        private string _fullPath = string.Empty;

        [ObservableProperty]
        private bool _isExpanded;

        /// <summary>
        /// 子节点集合（用于目录）
        /// </summary>
        public ObservableCollection<KnowledgeFileNode> Children { get; } = new();
    }
}
