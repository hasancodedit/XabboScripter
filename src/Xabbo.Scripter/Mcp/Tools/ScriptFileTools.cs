using System;
using System.IO;

using Xabbo.Scripter.Configuration;
using Xabbo.Scripter.Engine;
using Xabbo.Scripter.Services;
using Xabbo.Scripter.ViewModel;

namespace Xabbo.Scripter.Mcp.Tools;

public sealed class ScriptFileTools : IMcpToolProvider
{
    private readonly ScriptsViewManager _scripts;
    private readonly ScriptEngine _engine;
    private readonly IUiContext _ui;
    private readonly McpConfig _config;

    public ScriptFileTools(ScriptsViewManager scripts, ScriptEngine engine, IUiContext ui, McpConfig config)
    {
        _scripts = scripts;
        _engine = engine;
        _ui = ui;
        _config = config;
    }

    [McpTool("save_script", "Save a script to disk with the given file name and code, creating it or overwriting an existing one. If the script is open in a tab the editor is updated live.")]
    public object SaveScript(
        [McpParameter("Target file name (with or without .csx).")] string fileName,
        [McpParameter("The C# script source code.")] string code,
        [McpParameter("Allow overwriting an existing file. Defaults to false.")] bool overwrite = false)
    {
        McpGuard.Require(_config.AllowFileWrite, "file write");

        string normalized = Normalize(fileName);

        return _ui.Invoke(() =>
        {
            ScriptViewModel? existing = _scripts.FindByFileName(normalized);

            if (existing is null && File.Exists(Path.Combine(_engine.ScriptDirectory, normalized)) && !overwrite)
                throw new McpToolException($"A file named '{normalized}' already exists. Pass overwrite=true to replace it.");

            ScriptViewModel viewModel;

            if (existing is not null)
            {
                if (existing.IsSavedToDisk && !overwrite)
                    throw new McpToolException($"A script named '{normalized}' already exists. Pass overwrite=true to replace it.");

                existing.ReplaceCode(code);
                viewModel = existing;
            }
            else
            {
                viewModel = _scripts.NewScript(code, open: false);
                viewModel.FileName = normalized;
            }

            viewModel.Save();

            return new
            {
                saved = true,
                path = Path.Combine(_engine.ScriptDirectory, viewModel.FileName),
                script = ScriptInfo.Of(viewModel)
            };
        });
    }

    [McpTool("delete_script", "Delete a script from disk and remove it from the scripter. Running scripts cannot be deleted.")]
    public object DeleteScript(
        [McpParameter("The script file name (with or without .csx) or display name.")] string script)
    {
        McpGuard.Require(_config.AllowFileWrite, "file write");

        return _ui.Invoke(() =>
        {
            ScriptViewModel? viewModel = _scripts.FindScript(script);
            if (viewModel is null)
                throw new McpToolException($"No script found matching '{script}'.");

            if (viewModel.IsRunning)
                throw new McpToolException("Cannot delete a script while it is running.");

            _scripts.DeleteScript(viewModel);

            string path = Path.Combine(_engine.ScriptDirectory, viewModel.FileName);
            bool fileDeleted = false;
            if (File.Exists(path))
            {
                File.Delete(path);
                fileDeleted = true;
            }

            return new { deleted = true, fileName = viewModel.FileName, fileDeleted };
        });
    }

    [McpTool("rename_script", "Rename a script's file on disk.")]
    public object RenameScript(
        [McpParameter("The current script file name (with or without .csx) or display name.")] string script,
        [McpParameter("The new file name (with or without .csx).")] string newFileName)
    {
        McpGuard.Require(_config.AllowFileWrite, "file write");

        string normalized = Normalize(newFileName);

        return _ui.Invoke(() =>
        {
            ScriptViewModel? viewModel = _scripts.FindScript(script);
            if (viewModel is null)
                throw new McpToolException($"No script found matching '{script}'.");

            if (viewModel.IsRunning)
                throw new McpToolException("Cannot rename a script while it is running.");

            string target = Path.Combine(_engine.ScriptDirectory, normalized);
            if (File.Exists(target))
                throw new McpToolException($"A file named '{normalized}' already exists.");

            ScriptViewModel? clash = _scripts.FindByFileName(normalized);
            if (clash is not null && !ReferenceEquals(clash, viewModel))
                throw new McpToolException($"Another script already uses the name '{normalized}'.");

            if (viewModel.IsSavedToDisk)
            {
                string source = Path.Combine(_engine.ScriptDirectory, viewModel.FileName);
                if (File.Exists(source))
                    File.Move(source, target);
            }

            viewModel.FileName = normalized;
            if (viewModel.IsSavedToDisk)
                viewModel.Save();

            return new { renamed = true, fileName = normalized, script = ScriptInfo.Of(viewModel) };
        });
    }

    private static string Normalize(string fileName)
    {
        fileName = fileName.Trim();

        if (string.IsNullOrEmpty(fileName))
            throw new McpToolException("File name cannot be empty.");

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new McpToolException("File name contains invalid characters.");

        if (!fileName.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
            fileName += ".csx";

        return fileName;
    }
}
