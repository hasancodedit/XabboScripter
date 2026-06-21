using System;
using System.Collections.Generic;
using System.Linq;

using Xabbo.Scripter.Configuration;
using Xabbo.Scripter.Services;
using Xabbo.Scripter.ViewModel;

namespace Xabbo.Scripter.Mcp.Tools;

public sealed class AutostartTools : IMcpToolProvider
{
    private readonly AutostartService _autostart;
    private readonly ScriptsViewManager _scripts;
    private readonly IUiContext _ui;
    private readonly McpConfig _config;

    public AutostartTools(AutostartService autostart, ScriptsViewManager scripts, IUiContext ui, McpConfig config)
    {
        _autostart = autostart;
        _scripts = scripts;
        _ui = ui;
        _config = config;
    }

    [McpTool("list_autostart", "List the scripts configured to run automatically when the scripter connects to the game, with their current run status.")]
    public object ListAutostart()
    {
        return _ui.Invoke(() =>
        {
            List<object> tasks = _autostart.Tasks.Select(t => (object)new
            {
                name = t.Name,
                fileName = t.FileName,
                status = t.Status,
                running = t.IsRunning,
                valid = t.IsValid,
                addedAt = t.AddedAt
            }).ToList();

            return (object)new { count = tasks.Count, tasks };
        });
    }

    [McpTool("add_autostart", "Add a saved script to the autostart list so it runs automatically when the scripter connects.")]
    public object AddAutostart(
        [McpParameter("The script file name (with or without .csx) or display name. Must be saved to disk.")] string script)
    {
        McpGuard.Require(_config.AllowFileWrite, "file write");

        return _ui.Invoke(() =>
        {
            ScriptViewModel viewModel = _scripts.FindScript(script)
                ?? throw new McpToolException($"No script found matching '{script}'.");

            if (!viewModel.IsSavedToDisk)
                throw new McpToolException("Only scripts saved to disk can be added to autostart. Save it first.");

            viewModel.IsAutostart = true;
            return (object)new { added = true, fileName = viewModel.FileName };
        });
    }

    [McpTool("remove_autostart", "Remove a script from the autostart list.")]
    public object RemoveAutostart(
        [McpParameter("The script file name (with or without .csx) or display name.")] string script)
    {
        McpGuard.Require(_config.AllowFileWrite, "file write");

        return _ui.Invoke(() =>
        {
            ScriptViewModel? viewModel = _scripts.FindScript(script);
            string fileName = viewModel?.FileName
                ?? (script.EndsWith(".csx", StringComparison.OrdinalIgnoreCase) ? script : script + ".csx");

            _autostart.SetAutostart(fileName, false);
            return (object)new { removed = true, fileName };
        });
    }

    [McpTool("restart_autostart", "Restart an autostart task's script now (stops it if running, then runs it again).")]
    public object RestartAutostart(
        [McpParameter("The autostart script file name (with or without .csx) or display name.")] string script)
    {
        return _ui.Invoke(() =>
        {
            AutostartTaskViewModel task = FindTask(script);
            task.Restart();
            return (object)new { restarted = true, fileName = task.FileName };
        });
    }

    [McpTool("stop_autostart", "Stop a running autostart task's script.")]
    public object StopAutostart(
        [McpParameter("The autostart script file name (with or without .csx) or display name.")] string script)
    {
        return _ui.Invoke(() =>
        {
            AutostartTaskViewModel task = FindTask(script);
            task.Stop();
            return (object)new { stopped = true, fileName = task.FileName };
        });
    }

    private AutostartTaskViewModel FindTask(string reference)
    {
        string withExtension = reference.EndsWith(".csx", System.StringComparison.OrdinalIgnoreCase) ? reference : reference + ".csx";

        return _autostart.Tasks.FirstOrDefault(t =>
                   t.FileName.Equals(reference, System.StringComparison.OrdinalIgnoreCase) ||
                   t.FileName.Equals(withExtension, System.StringComparison.OrdinalIgnoreCase) ||
                   t.Name.Equals(reference, System.StringComparison.OrdinalIgnoreCase))
               ?? throw new McpToolException($"No autostart task found matching '{reference}'.");
    }
}
