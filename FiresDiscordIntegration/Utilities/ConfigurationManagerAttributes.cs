using System;
using BepInEx.Configuration;

/// <summary>
/// Display hints read by BepInEx.ConfigurationManager (matched by type name, so this
/// local copy stays in the global namespace). Null fields keep the default display.
/// </summary>
#pragma warning disable 0649, 0169
internal sealed class ConfigurationManagerAttributes
{
    public delegate void CustomHotkeyDrawerFunc(ConfigEntryBase setting, ref bool isCurrentlyAcceptingInput);

    public bool? ShowRangeAsPercent;
    public Action<ConfigEntryBase> CustomDrawer;
    public CustomHotkeyDrawerFunc CustomHotkeyDrawer;
    public bool? Browsable;
    public string Category;
    public object DefaultValue;
    public bool? HideDefaultButton;
    public bool? HideSettingName;
    public string Description;
    public string DispName;
    public int? Order;
    public bool? ReadOnly;
    public bool? IsAdvanced;
    public Func<object, string> ObjToStr;
    public Func<string, object> StrToObj;
}
#pragma warning restore 0649, 0169
