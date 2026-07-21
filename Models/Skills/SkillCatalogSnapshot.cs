using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Models.Skills;

public sealed class SkillCatalogSnapshot
{
    public static readonly SkillCatalogSnapshot Empty = new(Array.Empty<SkillDescriptor>());

    public SkillCatalogSnapshot(IReadOnlyList<SkillDescriptor> skills)
    {
        Skills = skills;
        EffectiveSkills = skills.Where(skill => skill.IsEffective && skill.IsEnabled && !skill.HasErrors).ToArray();
    }

    public IReadOnlyList<SkillDescriptor> Skills { get; }
    public IReadOnlyList<SkillDescriptor> EffectiveSkills { get; }
}
