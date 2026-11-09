using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop;

/// <summary>The native Save dialog for <c>shell.saveImage</c>, owned by the main window, fixed to *.png.</summary>
internal sealed class WinFormsSaveImagePrompt(Form owner) : ISaveImagePrompt
{
    private const int MaxAttempts = 3;

    public Task<PickedPath?> PickPathAsync(string defaultName, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PickedPath?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (owner.IsDisposed || ct.IsCancellationRequested)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }
        try
        {
            owner.BeginInvoke(() =>
            {
                try
                {
                    var name = defaultName;
                    for (var attempt = 0; attempt < MaxAttempts; attempt++)
                    {
                        if (owner.IsDisposed || ct.IsCancellationRequested) break;
                        using var dialog = new SaveFileDialog
                        {
                            Title = "Save image",
                            Filter = "PNG image (*.png)|*.png",
                            FilterIndex = 1,
                            DefaultExt = "png",
                            AddExtension = true,
                            OverwritePrompt = true,
                            CheckPathExists = true,
                            ValidateNames = true,
                            RestoreDirectory = true,
                            FileName = name,
                            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                        };
                        if (dialog.ShowDialog(owner) != DialogResult.OK) break;
                        var path = dialog.FileName;
                        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        {
                            // OverwritePrompt asked about exactly this path if it existed; only then may it be replaced.
                            tcs.TrySetResult(new PickedPath(path, File.Exists(path)));
                            return;
                        }
                        // Never rename: ask again for a .png name, so the overwrite prompt covers the real target.
                        MessageBox.Show(owner, "Choose a name ending in .png.", "Save image", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        name = Path.GetFileNameWithoutExtension(path) + ".png";
                    }
                    tcs.TrySetResult(null);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
                {
                    tcs.TrySetResult(null);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetResult(null);
        }
        return tcs.Task;
    }
}
