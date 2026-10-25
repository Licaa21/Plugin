using System.Diagnostics;
using System.Text.Json;
using Moonfin.Server.Models;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The functions on the Moonbase page that fill the "All TV app settings" tab and read it back decide what
/// gets written into the admin defaults: what "not set" is, what is trimmed, rounded or moved into range.
/// They are plain JavaScript, so they are taken out of configPage.html and run under node with a small stand-in
/// for the few DOM calls they make (no packages needed). Where node is not installed the test cannot run and
/// returns without checking anything, unless the CI variable is set, which makes it fail instead.
/// </summary>
public sealed class ClientDefaultsPageScriptTests
{
    private const string Harness = """
        const fs = require('fs');
        const html = fs.readFileSync(process.argv[2], 'utf8');
        const start = html.indexOf('var CLIENT_DEFAULTS_SCHEMA');
        const end = html.indexOf('// The holidays in seasonal-holidays.json');
        if (start < 0 || end < start) {
            console.log(JSON.stringify([{ name: 'find the page script', ok: false, detail: 'markers not found' }]));
            process.exit(0);
        }
        const code = html.slice(start, end);

        const controls = {};
        function control(tag, options) {
            return {
                tagName: tag, options: (options || []).map(v => ({ value: v })), selectedIndex: -1, _v: '', dataset: {},
                get value() { return this._v; },
                set value(v) {
                    v = String(v);
                    if (this.tagName === 'SELECT') {
                        this.selectedIndex = this.options.findIndex(o => o.value === v);
                        this._v = this.selectedIndex >= 0 ? v : '';
                    } else {
                        this._v = v;
                    }
                },
                appendChild(o) { this.options.push(o); },
            };
        }
        const doc = { getElementById: id => controls[id] || null, createElement: () => ({ value: '', textContent: '' }) };
        const escStart = html.lastIndexOf('function esc(str)', start);
        const escCode = html.slice(escStart, html.indexOf('\n            }', escStart) + 14);
        const api = new Function('document', escCode + '; ' + code +
            '; return { schema: CLIENT_DEFAULTS_SCHEMA, set: setClientDefaultValue, collect: collectClientDefaults, field: clientDefaultField, UNSET: CLIENT_DEFAULT_UNSET };')(doc);
        const U = api.UNSET;

        const scenarios = [
            { name: 'yes/no, nothing stored', f: { k: 'a', t: 'bool' }, tag: 'SELECT', opts: [U, 'true', 'false'], stored: null, expect: null },
            { name: 'yes/no, stored true', f: { k: 'a', t: 'bool' }, tag: 'SELECT', opts: [U, 'true', 'false'], stored: true, expect: true },
            { name: 'yes/no, stored false', f: { k: 'a', t: 'bool' }, tag: 'SELECT', opts: [U, 'true', 'false'], stored: false, expect: false },
            { name: 'yes/no, empty choice is not set', f: { k: 'a', t: 'bool' }, tag: 'SELECT', opts: [U, '', 'true'], typed: '', expect: null },
            { name: 'text is trimmed', f: { k: 'a', t: 'text' }, tag: 'INPUT', typed: '  dark  ', expect: 'dark' },
            { name: 'blank text is not set', f: { k: 'a', t: 'text' }, tag: 'INPUT', typed: '   ', expect: null },
            { name: 'a choice with an empty value is a real choice', f: { k: 'a', t: 'text' }, tag: 'SELECT', opts: [U, '', '#fff'], typed: '', expect: '' },
            { name: 'a dropdown left alone is not set', f: { k: 'a', t: 'text' }, tag: 'SELECT', opts: [U, '', '#fff'], stored: null, expect: null },
            { name: 'a whole number is rounded', f: { k: 'a', t: 'int' }, tag: 'INPUT', typed: '15.7', expect: 16 },
            { name: 'a stored value outside the range that nobody touched is saved as it was', f: { k: 'a', t: 'int', min: 20, max: 100 }, tag: 'INPUT', stored: 19, expect: 19, box: '19' },
            { name: 'a stored decimal on a whole number setting that nobody touched is saved as it was', f: { k: 'a', t: 'int' }, tag: 'INPUT', stored: 15.7, expect: 15.7 },
            { name: 'a stored value that is then changed is moved into range', f: { k: 'a', t: 'int', min: 20, max: 100 }, tag: 'INPUT', stored: 19, typed: '5', expect: 20 },
            { name: 'a number above the range is moved to the top', f: { k: 'a', t: 'int', min: 0, max: 100 }, tag: 'INPUT', typed: '500', expect: 100, box: '100' },
            { name: 'a number below the range is moved to the bottom', f: { k: 'a', t: 'int', min: 0, max: 100 }, tag: 'INPUT', typed: '-9', expect: 0, box: '0' },
            { name: 'a decimal is kept', f: { k: 'a', t: 'num' }, tag: 'INPUT', typed: '0.35', expect: 0.35 },
            { name: 'text in a number box is not set', f: { k: 'a', t: 'int' }, tag: 'INPUT', typed: 'abc', expect: null },
            { name: 'a number box the browser rejects keeps the stored value', f: { k: 'a', t: 'int' }, tag: 'INPUT', stored: 7, typed: '', bad: true, expect: 7 },
            { name: 'an impossible number is not set', f: { k: 'a', t: 'num' }, tag: 'INPUT', typed: '1e999', expect: null },
            { name: 'an empty choice on a number dropdown is not set, never 0', f: { k: 'a', t: 'int' }, tag: 'SELECT', opts: [U, '', '10'], typed: '', expect: null },
            { name: 'a number dropdown choice is a number', f: { k: 'a', t: 'int' }, tag: 'SELECT', opts: [U, '10', '20'], typed: '10', expect: 10 },
            { name: 'a list is one entry per line', f: { k: 'a', t: 'list' }, tag: 'TEXTAREA', typed: 'a\n b \r\n\nc', expect: ['a', 'b', 'c'] },
            { name: 'an empty list is not set', f: { k: 'a', t: 'list' }, tag: 'TEXTAREA', typed: '  \n ', expect: null },
            { name: 'a stored list shows one entry per line and comes back', f: { k: 'a', t: 'list' }, tag: 'TEXTAREA', stored: ['x', 'y'], expect: ['x', 'y'], box: 'x\ny' },
            { name: 'a stored value no choice has is kept', f: { k: 'a', t: 'text' }, tag: 'SELECT', opts: [U, 'small'], stored: 'weird', expect: 'weird', optionCount: 3 },
            { name: 'a stored empty value on a dropdown without an empty choice is kept', f: { k: 'a', t: 'text' }, tag: 'SELECT', opts: [U, 'small'], stored: '', expect: '', optionCount: 3 },
        ];

        const results = scenarios.map(sc => {
            api.schema.splice(0, api.schema.length, sc.f);
            const c = control(sc.tag, sc.opts);
            controls['ClientDefault_' + sc.f.k] = c;
            if ('stored' in sc) api.set(sc.f, sc.stored);
            if ('typed' in sc) c.value = sc.typed;
            if (sc.bad) c.validity = { badInput: true };
            const target = {};
            if ('stored' in sc && sc.bad) target[sc.f.k] = sc.stored;
            api.collect(target);
            const problems = [];
            if (JSON.stringify(target[sc.f.k]) !== JSON.stringify(sc.expect)) problems.push('wrote ' + JSON.stringify(target[sc.f.k]) + ', expected ' + JSON.stringify(sc.expect));
            if ('box' in sc && c.value !== sc.box) problems.push('the box shows ' + JSON.stringify(c.value) + ', expected ' + JSON.stringify(sc.box));
            if ('optionCount' in sc && c.options.length !== sc.optionCount) problems.push(c.options.length + ' choices, expected ' + sc.optionCount);
            return { name: sc.name, ok: problems.length === 0, detail: problems.join('; ') };
        });
        // drawing: a label, key and choices with quotes, ampersands and angle brackets must not break the markup
        api.schema.splice(0, api.schema.length);
        const markup = api.field({ k: 'x', t: 'text', l: 'Say "hi" & <b>', o: [['a"b', 'c<d>&e']] });
        const rendered = [
            { name: 'a label with quotes and angle brackets is escaped', ok: !markup.includes('<b>') && markup.includes('&lt;b&gt;') && markup.includes('&quot;hi&quot;') && markup.includes('&amp;'), detail: markup.slice(0, 160) },
            { name: 'a choice value with a quote stays inside its attribute', ok: markup.includes('value="a&quot;b"') && markup.includes('c&lt;d&gt;&amp;e'), detail: markup.slice(0, 200) },
        ];
        console.log(JSON.stringify(results.concat(rendered)));
        """;

    private sealed record Outcome(string Name, bool Ok, string Detail);

    [Fact]
    public void ThePagesSaveLogicWritesWhatTheAdminMeant()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "moonfin-page-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            using (var stream = typeof(MoonfinSettingsProfile).Assembly.GetManifestResourceStream("Moonfin.Server.Pages.configPage.html")!)
            using (var file = File.Create(Path.Combine(workDir, "configPage.html")))
            {
                stream.CopyTo(file);
            }

            File.WriteAllText(Path.Combine(workDir, "harness.js"), Harness);

            var start = new ProcessStartInfo("node")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workDir,
            };
            start.ArgumentList.Add("harness.js");
            start.ArgumentList.Add("configPage.html");

            Process process;
            try
            {
                process = Process.Start(start)!;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed: fine on a PC, but a CI run (GitHub sets CI) must not pass without checking anything.
                Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")), "node is needed for this test on CI");
                return;
            }

            using (process)
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    process.Kill(entireProcessTree: true);
                    Assert.Fail("node did not finish in 30 seconds");
                }

                var output = outputTask.Result;
                var error = errorTask.Result;
                Assert.True(process.ExitCode == 0, "node failed: " + error);

                var outcomes = JsonSerializer.Deserialize<List<Outcome>>(output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                Assert.True(outcomes.Count >= 25, "the scenarios did not run");
                var failed = outcomes.Where(o => !o.Ok).Select(o => $"{o.Name}: {o.Detail}").ToList();
                Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed));
            }
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
