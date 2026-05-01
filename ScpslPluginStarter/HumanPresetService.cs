using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using PlayerRoles;

namespace ScpslPluginStarter;

internal sealed class HumanPresetService
{
    public NamedLoadoutDefinition[] GetPresets(PluginConfig config)
    {
        NamedLoadoutDefinition[] presets = config.HumanLoadoutPresets ?? Array.Empty<NamedLoadoutDefinition>();
        if (presets.Length > 0)
        {
            return presets;
        }

        return new[]
        {
            new NamedLoadoutDefinition
            {
                Name = "Default",
                Description = WarmupLocalization.T("Fallback human preset.", "备用人类预设。"),
                Role = config.HumanRole,
                UseRoleDefaultLoadout = false,
                Loadout = config.HumanLoadout,
            },
        };
    }

    public NamedLoadoutDefinition? GetSelectedPreset(PluginConfig config, IReadOnlyDictionary<int, string> selectedPresets, Player player)
    {
        if (selectedPresets.TryGetValue(player.PlayerId, out string? selector))
        {
            NamedLoadoutDefinition? preset = FindPreset(config, selector);
            if (preset != null)
            {
                return preset;
            }
        }

        return GetPresets(config).FirstOrDefault();
    }

    public NamedLoadoutDefinition? FindPreset(PluginConfig config, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        selector = selector.Trim();
        NamedLoadoutDefinition[] presets = GetPresets(config);
        if (int.TryParse(selector, out int index))
        {
            int zeroBased = index - 1;
            if (zeroBased >= 0 && zeroBased < presets.Length)
            {
                return presets[zeroBased];
            }
        }

        NamedLoadoutDefinition? byName = presets.FirstOrDefault(preset =>
            string.Equals(preset.Name, selector, StringComparison.OrdinalIgnoreCase));
        if (byName != null)
        {
            return byName;
        }

        return presets.FirstOrDefault(preset =>
            string.Equals(preset.Role.ToString(), selector, StringComparison.OrdinalIgnoreCase));
    }

    public string GetSelectedPresetName(PluginConfig config, IReadOnlyDictionary<int, string> selectedPresets, Player player)
    {
        return GetSelectedPreset(config, selectedPresets, player)?.Name ?? "Default";
    }

    public string BuildMenu(PluginConfig config, IReadOnlyDictionary<int, string> selectedPresets, Player player)
    {
        string selectedName = GetSelectedPresetName(config, selectedPresets, player);
        NamedLoadoutDefinition[] presets = GetPresets(config);
        List<string> lines = new()
        {
            WarmupLocalization.T($"Presets (selected: {selectedName})", $"预设（当前：{selectedName}）"),
        };

        for (int i = 0; i < presets.Length; i++)
        {
            NamedLoadoutDefinition preset = presets[i];
            string marker = string.Equals(selectedName, preset.Name, StringComparison.OrdinalIgnoreCase) ? "*" : "-";
            string gearLabel = preset.UseRoleDefaultLoadout
                ? WarmupLocalization.T("default gear", "默认装备")
                : WarmupLocalization.T("custom gear", "自定义装备");
            lines.Add($"{marker} {i + 1}. {preset.Name} [{preset.Role}] - {gearLabel} - {preset.Description}");
        }

        lines.Add(WarmupLocalization.T("Use `loadout <number|name>` to apply.", "使用 `loadout <编号|名称>` 应用。"));
        return string.Join("\n", lines);
    }
}
