using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Gui;
using Cosmoteer.Game.Gui.Build;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Blueprints;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.UI;
using Halfling.Gui;
using Halfling.Gui.Components.Droppers;
using HarmonyLib;

namespace CrewlessLogistics.Belts;

/// <summary>
/// Client-only filter UI restriction (docs/BELTS_DESIGN.md §20 D4): the popout offers only the choices the connector's
/// neighbours allow. The engine's per-choice lock (GameModeManager.IsToggleChoiceLocked) is per PartRules, so these
/// postfixes narrow the per-instance choice sets instead. Sim validity never depends on them: BeltManager normalises.
/// </summary>
internal static class FilterUi
{
    private static readonly ID<PartToggleGuiRules> Toggle = new(BeltState.FilterToggleId);

    public static MethodBase[] Targets() => new MethodBase[]
    {
        AccessTools.Method(typeof(PartPopoutToggleButton), "UpdateDisplayedToggleValues")
            ?? throw new MissingMethodException("PartPopoutToggleButton.UpdateDisplayedToggleValues"),
        AccessTools.Method(typeof(SelectPartsState), "UpdateToggleButtons")
            ?? throw new MissingMethodException("SelectPartsState.UpdateToggleButtons"),
        // Popouts lay every choice out in one row (TileBottomLeftHorizontal, wrap: false; PartPopoutToggleButton.cs:35,
        // PartPopoutBuildToggleButton.cs:36): dozens of modded resources would run off the screen.
        AccessTools.Constructor(typeof(PartPopoutToggleButton), new[] { typeof(BaseCommandCard), typeof(GameGui), typeof(PartToggleGuiRules) })
            ?? throw new MissingMethodException("PartPopoutToggleButton..ctor"),
        AccessTools.Constructor(typeof(PartPopoutBuildToggleButton), new[] { typeof(GameGui), typeof(PartToggleGuiRules) })
            ?? throw new MissingMethodException("PartPopoutBuildToggleButton..ctor"),
    };

    /// <summary>Non-fatal: a failure leaves the vanilla full list (sim normalisation still applies).</summary>
    public static void Apply(Harmony harmony)
    {
        try
        {
            MethodBase[] t = Targets();
            Patcher.RefuseInlinedTargets(t);
            harmony.Patch(t[0], prefix: new HarmonyMethod(typeof(FilterUi), nameof(LivePrefix)));
            harmony.Patch(t[1], postfix: new HarmonyMethod(typeof(FilterUi), nameof(BuildPostfix)));
            harmony.Patch(t[2], postfix: new HarmonyMethod(typeof(FilterUi), nameof(LivePopoutPostfix)));
            harmony.Patch(t[3], postfix: new HarmonyMethod(typeof(FilterUi), nameof(BuildPopoutPostfix)));
            Log.Info("filter UI patched: " + string.Join(", ", Array.ConvertAll(t, m => m.DeclaringType?.Name + "." + m.Name)));
        }
        catch (Exception ex)
        {
            Log.Error("filter UI patch (full choice list stays)", ex);
        }
    }

    public static void LivePopoutPostfix(PartPopoutToggleButton __instance) => Wrap(__instance, __instance.Rules.ToggleID, __instance._choiceButtons);

    public static void BuildPopoutPostfix(PartPopoutBuildToggleButton __instance) => Wrap(__instance, __instance.Rules.ToggleID, __instance._choiceButtons);

    /// <summary>
    /// Belt filter popouts only (one choice per resource type): the vanilla wrapping variant of the same layout, its
    /// width capped to the room beside the button (the popout's own AutoSize bound is the whole window), kept up to date
    /// whenever the button moves or the popout is (re)parented. The anchor's native flip to the other side of the button
    /// is off by default (PreferredHorizontalSpace 0); unbounded, it flips whenever the popout does not fit on its side.
    /// </summary>
    private static void Wrap(Widget button, ID<PartToggleGuiRules> toggle, List<ListItem> choices)
    {
        try
        {
            if (!BeltState.IsFilter(toggle, out _) || choices.Count == 0 || choices[0].Parent is not LayoutBox box)
            {
                return;
            }
            box.Children.LayoutAlgorithm = LayoutAlgorithms.TileBottomLeftHorizontalWrap;
            for (int i = 0; i < box.Components.Count; i++)
            {
                if (box.Components[i] is WidgetRelativeRectController anchor)
                {
                    anchor.PreferredHorizontalSpace = float.MaxValue;
                }
            }
            void Fit(object? sender, EventArgs e)
            {
                if ((box.Parent?.RectProvider ?? box.AutoSize.Bounds)?.ScreenRect is { } screen) // the rect the anchor measures
                {
                    Halfling.Geometry.Rect b = button.ScreenRect;
                    box.AutoSize.WidthValueType = Halfling.Gui.Components.AutoSizing.ValueInterpretationType.Absolute;
                    box.AutoSize.MaxWidth = BeltRenderState.PopoutWidth(screen.Left, screen.Right, b.Left, b.Right);
                }
            }
            button.ScreenRectChanged += Fit;
            box.Parented += Fit;
            Fit(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("filter UI wrap", ex);
        }
    }

    /// <summary>Live ship selection: drop choices no selected connector allows (union, like vanilla multi-select).</summary>
    public static void LivePrefix(PartPopoutToggleButton __instance, HashSet<int> availableModes)
    {
        try
        {
            if (__instance.Rules.ToggleID != Toggle)
            {
                return;
            }
            var allowed = new List<BigInteger>();
            var toggles = new List<PartUIToggle>();
            if (__instance.Sim.PlayerInput.SelectedParts.Count > 0)
            {
                foreach (Part p in __instance.Sim.PlayerInput.SelectedParts)
                {
                    if (PartUIToggle.GetOnPart(p, Toggle) is { } t)
                    {
                        toggles.Add(t);
                    }
                }
            }
            else
            {
                foreach (Ship s in __instance.Sim.PlayerInput.SelectedShips)
                {
                    PartUIToggle.GetAllOnShip(s, Toggle, toggles);
                }
            }
            foreach (PartUIToggle t in toggles)
            {
                allowed.Add(t.Part is { } part && BeltState.TryGet(part, out NodeState? st) ? st.Allowed : BeltTypes.AllMask);
            }
            availableModes.RemoveWhere(m => !allowed.Exists(a => BeltTypes.ChoiceAllowed(a, m)));
        }
        catch (Exception ex)
        {
            Log.Error("filter UI live", ex);
        }
    }

    /// <summary>Build mode: narrow the choices and show the normalised value (display only; the sim sets it once built).</summary>
    public static void BuildPostfix(SelectPartsState __instance)
    {
        try
        {
            foreach (BasePartBuildToggleButton b in __instance._toggleButtons)
            {
                if (b.Rules.ToggleID != Toggle || !b.SelfActive)
                {
                    continue;
                }
                bool editing = __instance.IsEditingBlueprints;
                var offered = new HashSet<int>();
                var shown = new HashSet<int>();
                foreach (BlueprintPart p in __instance._selectedParts)
                {
                    if (BeltState.KindOf(p.Rules) != NodeKind.Connector || p.Ship is not { } ship)
                    {
                        continue;
                    }
                    BigInteger a = PartFlow.ForConnector(p, cell => ship.BlueprintParts[cell], _ => false, q => PartFlow.BlueprintModes(q, editing));
                    int mode = ship.Metadata.PartUIToggleStates.Get((new PartSaveData(p), Toggle), editing) ?? 0;
                    shown.Add(BeltTypes.Normalize(a, mode));
                    foreach (int m in b._availableChoices)
                    {
                        if (BeltTypes.ChoiceAllowed(a, m))
                        {
                            offered.Add(m);
                        }
                    }
                }
                if (shown.Count == 0)
                {
                    continue;
                }
                // Guard like vanilla (SelectPartsState.cs:952): a displayed-mode change must not be applied to the parts.
                __instance._updatingToggleButtons = true;
                try
                {
                    b.SetAvailableChoices(offered);
                    b.SetMultipleSelected(shown);
                }
                finally
                {
                    __instance._updatingToggleButtons = false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("filter UI build", ex);
        }
    }
}
