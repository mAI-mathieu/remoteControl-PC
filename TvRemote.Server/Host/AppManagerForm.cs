using TvRemote.Configuration;
using TvRemote.Services;

namespace TvRemote.Host;

public sealed class AppManagerForm : Form
{
    private readonly IConfigStore store;
    private readonly List<AppShortcut> draft;
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly Button edit, remove, up, down;
    private readonly ProgramImageList programImages = new();
    private readonly Icon brandIcon = BrandIcon.Load();

    public AppManagerForm(IConfigStore store)
    {
        this.store = store;
        lock (store.Current) draft = store.Current.AppShortcuts.ToList();
        Text = "Manage apps — TV Remote"; Font = new("Segoe UI", 10); Size = new(920, 600); MinimumSize = new(760, 460);
        Icon = brandIcon;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Apps on your remote", Font = new("Segoe UI", 18, FontStyle.Bold), AutoSize = true, Margin = new(0, 0, 0, 12) });
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new(0, 0, 0, 12) };
        toolbar.Controls.Add(ActionButton("Add program…", AddProgram)); toolbar.Controls.Add(ActionButton("Add website…", () => Add(new AppShortcut("", "", "globe", "url"))));
        edit = ActionButton("Edit…", Edit); remove = ActionButton("Remove", Remove);
        up = ActionButton("Move up", () => MoveShortcut(-1)); down = ActionButton("Move down", () => MoveShortcut(1));
        toolbar.Controls.AddRange([edit, remove, up, down]); layout.Controls.Add(toolbar);
        list.SmallImageList = programImages.Images;
        list.Columns.Add("Name", 165); list.Columns.Add("Type", 110); list.Columns.Add("Status", 140); list.Columns.Add("Program / website", 395);
        list.SelectedIndexChanged += (_, _) => UpdateButtons(); list.DoubleClick += (_, _) => Edit();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.F2) { Edit(); e.Handled = true; } };
        layout.Controls.Add(list);
        layout.Controls.Add(new Label { Text = "Changes appear on your phone when you save. Removing a shortcut keeps the installed program.\nChoose Move up / Move down to set the order on your remote.", AutoSize = true, Margin = new(0, 12, 0, 12) });
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = ActionButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); });
        var save = ActionButton("Save changes", Save); footer.Controls.Add(save); footer.Controls.Add(cancel); layout.Controls.Add(footer);
        AcceptButton = save; CancelButton = cancel; RefreshList();
    }
    internal static Button ActionButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new(100, 36), Padding = new(8, 0, 8, 0) };
        button.Click += (_, _) => action(); return button;
    }
    private int Selected => list.SelectedIndices.Count == 1 ? list.SelectedIndices[0] : -1;
    private void UpdateButtons()
    {
        var index = Selected; edit.Enabled = remove.Enabled = index >= 0; up.Enabled = index > 0; down.Enabled = index >= 0 && index < draft.Count - 1;
    }
    private void RefreshList(int selected = -1)
    {
        list.BeginUpdate(); list.Items.Clear(); programImages.Clear();
        foreach (var app in draft)
        {
            var resolved = AppLauncherService.Resolve(app);
            list.Items.Add(new ListViewItem([app.Name, app.Type == "url" ? "Website" : "Program", resolved != null ? "Ready" : "Missing file", app.Url ?? app.Path ?? ""]) { ImageIndex = app.Type == "executable" ? programImages.Add(resolved) : -1 });
        }
        list.EndUpdate();
        if (selected >= 0 && selected < list.Items.Count) { list.Items[selected].Selected = true; list.Items[selected].EnsureVisible(); }
        UpdateButtons();
    }
    private void AddProgram()
    {
        using var picker = new InstalledAppPickerForm();
        if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedApp is { } app) Add(new("", app.Name, "folder", "executable", app.Path));
    }
    private void Add(AppShortcut template)
    {
        if (draft.Count >= 100) { MessageBox.Show(this, "You can add up to 100 shortcuts.", "App limit"); return; }
        using var editor = new ShortcutEditorForm(template, true);
        if (editor.ShowDialog(this) == DialogResult.OK) { draft.Add(editor.Shortcut!); RefreshList(draft.Count - 1); }
    }
    private void Edit()
    {
        var index = Selected; if (index < 0) return;
        using var editor = new ShortcutEditorForm(draft[index], false);
        if (editor.ShowDialog(this) == DialogResult.OK) { draft[index] = editor.Shortcut!; RefreshList(index); }
    }
    private void Remove() { var index = Selected; if (index < 0) return; draft.RemoveAt(index); RefreshList(Math.Min(index, draft.Count - 1)); }
    private void MoveShortcut(int offset)
    {
        var index = Selected; var next = index + offset; if (index < 0 || next < 0 || next >= draft.Count) return;
        (draft[index], draft[next]) = (draft[next], draft[index]); RefreshList(next);
    }
    private void Save()
    {
        try { ShortcutManagement.Save(store, draft); DialogResult = DialogResult.OK; Close(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { MessageBox.Show(this, ex.Message, "Could not save apps", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    protected override void Dispose(bool disposing) { if (disposing) { programImages.Dispose(); brandIcon.Dispose(); } base.Dispose(disposing); }
}

internal sealed class ShortcutEditorForm : Form
{
    private readonly AppShortcut original;
    private readonly bool adding;
    private readonly TextBox name = new() { Dock = DockStyle.Fill, MaxLength = 60 };
    private readonly TextBox target = new() { Dock = DockStyle.Fill };
    private readonly ComboBox icon = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", ValueMember = "Id" };
    private readonly PictureBox programIcon = new() { Size = new(40, 40), SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label programIconHint = new() { AutoSize = true, Margin = new(8, 10, 0, 0) };
    private readonly Icon brandIcon = BrandIcon.Load();
    public AppShortcut? Shortcut { get; private set; }
    private sealed record IconOption(string Id, string Name);
    public ShortcutEditorForm(AppShortcut app, bool adding)
    {
        original = app; this.adding = adding;
        Icon = brandIcon;
        Text = adding ? (app.Type == "url" ? "Add website" : "Add program") : "Edit shortcut";
        Font = new("Segoe UI", 10); ClientSize = new(580, 315); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100)); layout.ColumnStyles.Add(new(SizeType.AutoSize)); Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Name on the remote", AutoSize = true }, 0, 0); layout.Controls.Add(name, 0, 1); layout.SetColumnSpan(name, 2); name.Text = app.Name;
        layout.Controls.Add(new Label { Text = app.Type == "url" ? "Website address" : "Program (.exe)", AutoSize = true, Margin = new(3, 12, 3, 3) }, 0, 2);
        layout.Controls.Add(target, 0, 3); target.Text = app.Url ?? app.Path ?? "";
        if (app.Type == "executable") layout.Controls.Add(AppManagerForm.ActionButton("Browse…", Browse), 1, 3);
        layout.Controls.Add(new Label { Text = app.Type == "executable" ? "Program icon (automatic)" : "Icon", AutoSize = true, Margin = new(3, 12, 3, 3) }, 0, 4);
        var options = new[] { new IconOption("folder", "App"), new("gamepad", "Games"), new("film", "Movies / TV"), new("music", "Music"), new("play", "Video"), new("globe", "Website") };
        icon.Items.AddRange(options); icon.SelectedIndex = Math.Max(0, Array.FindIndex(options, option => option.Id == app.Icon));
        if (app.Type == "url") { layout.Controls.Add(icon, 0, 5); layout.SetColumnSpan(icon, 2); }
        else
        {
            var preview = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            preview.Controls.Add(programIcon); preview.Controls.Add(programIconHint); layout.Controls.Add(preview, 0, 5); layout.SetColumnSpan(preview, 2);
            target.TextChanged += (_, _) => RefreshProgramIcon(); RefreshProgramIcon();
        }
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new(0, 18, 0, 0) };
        var save = AppManagerForm.ActionButton(adding ? "Add shortcut" : "Save shortcut", Save);
        var cancel = AppManagerForm.ActionButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); });
        footer.Controls.Add(save); footer.Controls.Add(cancel); layout.Controls.Add(footer, 0, 6); layout.SetColumnSpan(footer, 2); AcceptButton = save; CancelButton = cancel;
    }
    private void Browse()
    {
        using var file = new OpenFileDialog { Title = "Choose a program", Filter = "Programs (*.exe)|*.exe", CheckFileExists = true };
        if (file.ShowDialog(this) == DialogResult.OK) { target.Text = file.FileName; if (name.Text.Trim().Length == 0) name.Text = Path.GetFileNameWithoutExtension(file.FileName); }
    }
    private void Save()
    {
        try
        {
            var app = ShortcutManagement.Create(name.Text, ((IconOption)icon.SelectedItem!).Id, original.Type, target.Text, adding ? null : original.Id);
            if (app.Type == "executable" && !InstalledAppDiscovery.IsLocalProgram(app.Path)) throw new InvalidDataException("The program was not found. Use Browse to choose its .exe file.");
            Shortcut = app; DialogResult = DialogResult.OK; Close();
        }
        catch (InvalidDataException ex) { MessageBox.Show(this, ex.Message, "Check shortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void RefreshProgramIcon()
    {
        var image = ProgramIcons.GetBitmap(Environment.ExpandEnvironmentVariables(target.Text.Trim().Trim('"')));
        var previous = programIcon.Image; programIcon.Image = image; previous?.Dispose();
        programIconHint.Text = image != null ? "Uses this program’s Windows icon" : "Choose a program file to load its icon";
    }
    protected override void Dispose(bool disposing) { if (disposing) { programIcon.Image?.Dispose(); icon.Dispose(); brandIcon.Dispose(); } base.Dispose(disposing); }
}

internal sealed class InstalledAppPickerForm : Form
{
    private readonly TextBox search = new() { Dock = DockStyle.Fill, PlaceholderText = "Search installed programs…" };
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly Label status = new() { AutoSize = true, Text = "Finding programs…" };
    private readonly Button choose;
    private readonly ProgramImageList programImages = new();
    private readonly Icon brandIcon = BrandIcon.Load();
    private InstalledApp[] apps = [];
    public InstalledApp? SelectedApp { get; private set; }
    public InstalledAppPickerForm()
    {
        Text = "Choose a program"; Font = new("Segoe UI", 10); Size = new(760, 510); MinimumSize = new(600, 420); StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        Icon = brandIcon;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); Controls.Add(layout);
        search.Margin = new(0, 0, 0, 12); layout.Controls.Add(search); list.SmallImageList = programImages.Images; list.Columns.Add("Program", 230); list.Columns.Add("Location", 450); layout.Controls.Add(list);
        status.Margin = new(0, 8, 0, 8); layout.Controls.Add(status);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        choose = AppManagerForm.ActionButton("Choose program", Choose); choose.Enabled = false;
        var cancel = AppManagerForm.ActionButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); });
        footer.Controls.Add(choose); footer.Controls.Add(cancel); footer.Controls.Add(AppManagerForm.ActionButton("Browse files…", Browse)); layout.Controls.Add(footer); AcceptButton = choose; CancelButton = cancel;
        search.TextChanged += (_, _) => RefreshList(); list.SelectedIndexChanged += (_, _) => choose.Enabled = list.SelectedItems.Count == 1; list.DoubleClick += (_, _) => Choose();
        Shown += async (_, _) =>
        {
            try { var found = await InstalledAppDiscovery.FindAsync(); if (IsDisposed) return; apps = found; RefreshList(); }
            catch { if (!IsDisposed) status.Text = "Could not find programs automatically. Choose Browse files."; }
        };
    }
    private void RefreshList()
    {
        list.BeginUpdate(); list.Items.Clear(); programImages.Clear();
        foreach (var app in apps.Where(a => a.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase))) list.Items.Add(new ListViewItem([app.Name, app.Path]) { Tag = app, ImageIndex = programImages.Add(app.Path) });
        list.EndUpdate(); choose.Enabled = false; status.Text = $"{list.Items.Count} programs found. Use Browse files for portable apps or programs not listed.";
    }
    private void Choose() { if (list.SelectedItems.Count != 1) return; SelectedApp = (InstalledApp)list.SelectedItems[0].Tag!; DialogResult = DialogResult.OK; Close(); }
    private void Browse()
    {
        using var file = new OpenFileDialog { Title = "Choose a program or its shortcut", Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|Programs (*.exe)|*.exe", CheckFileExists = true };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        var app = InstalledAppDiscovery.FromFile(file.FileName);
        if (app == null) { MessageBox.Show(this, "Choose a local .exe file. Shortcuts with scripts, arguments or special launch commands are not supported.", "Choose a program"); return; }
        SelectedApp = app; DialogResult = DialogResult.OK; Close();
    }
    protected override void Dispose(bool disposing) { if (disposing) { programImages.Dispose(); brandIcon.Dispose(); } base.Dispose(disposing); }
}

internal sealed class ProgramImageList : IDisposable
{
    public ImageList Images { get; } = new() { ImageSize = new(24, 24), ColorDepth = ColorDepth.Depth32Bit };
    private readonly List<Bitmap> bitmaps = [];
    public int Add(string? path)
    {
        var bitmap = ProgramIcons.GetBitmap(path); if (bitmap == null) return -1;
        var index = Images.Images.Count; Images.Images.Add(bitmap); bitmaps.Add(bitmap); return index;
    }
    public void Clear() { Images.Images.Clear(); foreach (var bitmap in bitmaps) bitmap.Dispose(); bitmaps.Clear(); }
    public void Dispose() { Images.Dispose(); foreach (var bitmap in bitmaps) bitmap.Dispose(); bitmaps.Clear(); }
}
