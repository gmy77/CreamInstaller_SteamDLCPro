using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CreamInstaller.Components;
using CreamInstaller.Utility;

namespace CreamInstaller.Forms;

internal sealed partial class SelectDialogForm : CustomForm
{
    private readonly List<(Platform platform, string id, string name)> selected = new();

    internal SelectDialogForm(IWin32Window owner) : base(owner) => InitializeComponent();

    internal List<(Platform platform, string id, string name)> QueryUser(string groupBoxText,
        List<(Platform platform, string id, string name, bool alreadySelected)> choices)
    {
        if (!choices.Any())
            return null;
        groupBox.Text = groupBoxText;
        allCheckBox.Enabled = false;
        acceptButton.Enabled = false;
        selectionTreeView.AfterCheck += OnTreeNodeChecked;

        foreach (IGrouping<Platform, (Platform platform, string id, string name, bool alreadySelected)> group
                 in choices.GroupBy(c => c.platform).OrderBy(g => PlatformOrder(g.Key)))
        {
            string platformName = PlatformDisplayName(group.Key);
            TreeNode platformNode = new() { Tag = group.Key, Name = "$platform_" + group.Key, Text = platformName };
            foreach ((Platform platform, string id, string name, bool alreadySelected) in group.OrderBy(c => c.name))
            {
                TreeNode gameNode = new() { Tag = platform, Name = id, Text = name, Checked = alreadySelected };
                _ = platformNode.Nodes.Add(gameNode);
                if (alreadySelected)
                    selected.Add((platform, id, name));
            }
            platformNode.Checked = platformNode.Nodes.Count > 0
                                && platformNode.Nodes.Cast<TreeNode>().All(n => n.Checked);
            _ = selectionTreeView.Nodes.Add(platformNode);
            platformNode.Expand();
        }

        if (!selected.Any())
            OnLoad(null, null);

        UpdateAllCheckBox();
        allCheckBox.Enabled = true;
        acceptButton.Enabled = selected.Any();
        saveButton.Enabled = acceptButton.Enabled;
        loadButton.Enabled = ProgramData.ReadProgramChoices() is not null;
        OnResize(null, null);
        Resize += OnResize;
        return ShowDialog() == DialogResult.OK ? selected : null;
    }

    private static string PlatformDisplayName(Platform p) => p switch
    {
        Platform.Steam => "Steam",
        Platform.Epic => "Epic Games",
        Platform.Ubisoft => "Ubisoft Connect",
        Platform.Paradox => "Paradox Launcher",
        _ => p.ToString()
    };

    private static int PlatformOrder(Platform p) => p switch
    {
        Platform.Steam => 0,
        Platform.Epic => 1,
        Platform.Ubisoft => 2,
        Platform.Paradox => 3,
        _ => 99
    };

    private void OnTreeNodeChecked(object sender, TreeViewEventArgs e) => OnTreeNodeChecked(e.Node);

    private void OnTreeNodeChecked(TreeNode node)
    {
        if (node.Name.StartsWith("$platform_", StringComparison.Ordinal))
        {
            // Platform header: propagate checked state to all children
            selectionTreeView.AfterCheck -= OnTreeNodeChecked;
            foreach (TreeNode child in node.Nodes)
            {
                child.Checked = node.Checked;
                UpdateSelected(child);
            }
            selectionTreeView.AfterCheck += OnTreeNodeChecked;
        }
        else
        {
            UpdateSelected(node);
            // Keep platform node in sync with its children
            if (node.Parent is { } parent)
            {
                selectionTreeView.AfterCheck -= OnTreeNodeChecked;
                parent.Checked = parent.Nodes.Cast<TreeNode>().All(n => n.Checked);
                selectionTreeView.AfterCheck += OnTreeNodeChecked;
            }
        }
        UpdateAllCheckBox();
        acceptButton.Enabled = selected.Any();
        saveButton.Enabled = acceptButton.Enabled;
    }

    private void UpdateSelected(TreeNode gameNode)
    {
        Platform platform = (Platform)gameNode.Tag;
        string id = gameNode.Name;
        if (gameNode.Checked)
        {
            if (!selected.Any(s => s.platform == platform && s.id == id))
                selected.Add((platform, id, gameNode.Text));
        }
        else
            _ = selected.RemoveAll(s => s.platform == platform && s.id == id);
    }

    private void UpdateAllCheckBox()
    {
        bool allChecked = selectionTreeView.Nodes.Cast<TreeNode>()
            .SelectMany(n => n.Nodes.Cast<TreeNode>())
            .All(n => n.Checked);
        allCheckBox.CheckedChanged -= OnAllCheckBoxChanged;
        allCheckBox.Checked = allChecked;
        allCheckBox.CheckedChanged += OnAllCheckBoxChanged;
    }

    private void OnResize(object s, EventArgs e)
        => Text = TextRenderer.MeasureText(Program.ApplicationName, Font).Width > Size.Width - 100
            ? Program.ApplicationNameShort
            : Program.ApplicationName;

    private void OnSortCheckBoxChanged(object sender, EventArgs e)
    {
        selectionTreeView.TreeViewNodeSorter = sortCheckBox.Checked ? PlatformIdComparer.NodeText : null;
        selectionTreeView.Sort();
    }

    private void OnAllCheckBoxChanged(object sender, EventArgs e)
    {
        bool shouldCheck = selectionTreeView.Nodes.Cast<TreeNode>()
            .SelectMany(n => n.Nodes.Cast<TreeNode>())
            .Any(n => !n.Checked);
        selectionTreeView.AfterCheck -= OnTreeNodeChecked;
        foreach (TreeNode platformNode in selectionTreeView.Nodes)
        {
            platformNode.Checked = shouldCheck;
            foreach (TreeNode gameNode in platformNode.Nodes)
            {
                gameNode.Checked = shouldCheck;
                UpdateSelected(gameNode);
            }
        }
        selectionTreeView.AfterCheck += OnTreeNodeChecked;
        allCheckBox.CheckedChanged -= OnAllCheckBoxChanged;
        allCheckBox.Checked = shouldCheck;
        allCheckBox.CheckedChanged += OnAllCheckBoxChanged;
        acceptButton.Enabled = selected.Any();
        saveButton.Enabled = acceptButton.Enabled;
    }

    private void OnLoad(object sender, EventArgs e)
    {
        List<(Platform platform, string id)> choices = ProgramData.ReadProgramChoices().ToList();
        if (!choices.Any())
            return;
        selectionTreeView.AfterCheck -= OnTreeNodeChecked;
        foreach (TreeNode platformNode in selectionTreeView.Nodes)
            foreach (TreeNode gameNode in platformNode.Nodes)
            {
                gameNode.Checked = choices.Any(c => c.platform == (Platform)gameNode.Tag && c.id == gameNode.Name);
                UpdateSelected(gameNode);
            }
        foreach (TreeNode platformNode in selectionTreeView.Nodes)
            platformNode.Checked = platformNode.Nodes.Cast<TreeNode>().All(n => n.Checked);
        selectionTreeView.AfterCheck += OnTreeNodeChecked;
        UpdateAllCheckBox();
        acceptButton.Enabled = selected.Any();
        saveButton.Enabled = acceptButton.Enabled;
    }

    private void OnSave(object sender, EventArgs e)
    {
        ProgramData.WriteProgramChoices(
            selectionTreeView.Nodes.Cast<TreeNode>()
                .SelectMany(n => n.Nodes.Cast<TreeNode>())
                .Where(n => n.Checked)
                .Select(n => ((Platform)n.Tag, n.Name))
                .ToList());
        loadButton.Enabled = ProgramData.ReadProgramChoices() is not null;
    }

    private void OnSearchTextChanged(object sender, EventArgs e)
    {
        string q = searchTextBox.Text.Trim();
        selectionTreeView.BeginUpdate();
        try
        {
            foreach (TreeNode platformNode in selectionTreeView.Nodes)
            {
                bool anyMatch = false;
                foreach (TreeNode gameNode in platformNode.Nodes)
                {
                    bool match = string.IsNullOrEmpty(q)
                              || gameNode.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
                    gameNode.ForeColor = match ? ThemeManager.TextPrimary : ThemeManager.TextDisabled;
                    if (match) anyMatch = true;
                }
                platformNode.ForeColor = anyMatch || string.IsNullOrEmpty(q)
                    ? ThemeManager.TextPrimary
                    : ThemeManager.TextDisabled;
                if (!string.IsNullOrEmpty(q))
                {
                    if (anyMatch) platformNode.Expand();
                    else platformNode.Collapse();
                }
                else
                    platformNode.Expand();
            }
        }
        finally
        {
            selectionTreeView.EndUpdate();
        }
    }
}
