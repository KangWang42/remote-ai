using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace RemoteCli {
/// Selects one complete permission suggestion whose scope can be shown and saved from the phone.
/// Never constructs a broader replacement for a rule that cannot be saved.
public static class ClaudePermissions {
    static string Text(Dictionary<string, object> map, string key) {
        object value;
        return map.TryGetValue(key, out value) ? value as string ?? "" : "";
    }
    static bool Rule(object value) {
        var rule = value as Dictionary<string, object>;
        if (rule == null || Text(rule, "toolName").Length == 0) return false;
        object raw;
        if (rule.TryGetValue("ruleContent", out raw) && !(raw is string)) return false;
        string tool = Text(rule, "toolName"), content = Text(rule, "ruleContent");
        if (tool != "Bash" && tool != "PowerShell") return true;
        if (String.IsNullOrWhiteSpace(content) || content.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return false;
        int star = content.IndexOf('*');
        // Shell globs and regex stars are permission wildcards too, even inside quotes.
        // Only a single trailing prefix wildcard (including the legacy :*) is offered for saving.
        return star < 0 || (star == content.Length - 1 && star == content.LastIndexOf('*') && star > 1
            && (Char.IsWhiteSpace(content[star - 1]) || content[star - 1] == ':')
            && !String.IsNullOrWhiteSpace(content.Substring(0, star - 1)));
    }
    public static Dictionary<string, object> Select(object suggestions, bool allowSession) {
        var list = suggestions as IEnumerable;
        if (list == null || suggestions is string || suggestions is IDictionary) return null;
        foreach (object value in list) {
            var map = value as Dictionary<string, object>;
            if (map == null || Text(map, "type") != "addRules" || Text(map, "behavior") != "allow") continue;
            string destination = Text(map, "destination");
            if (destination != "localSettings" && !(allowSession && destination == "session")) continue;
            object raw;
            if (!map.TryGetValue("rules", out raw) || !(raw is IEnumerable) || raw is string || raw is IDictionary) continue;
            var rules = ((IEnumerable)raw).Cast<object>().ToArray();
            if (rules.Length == 0 || rules.Length > 4 || !rules.All(Rule)) continue;
            if (Detail(map).Length > 600) continue;
            return map;
        }
        return null;
    }
    public static string Detail(Dictionary<string, object> suggestion) {
        var rules = ((IEnumerable)suggestion["rules"]).Cast<object>().Cast<Dictionary<string, object>>()
            .Select(rule => Text(rule, "toolName") + "(" + (Text(rule, "ruleContent").Length == 0 ? "全部操作" : Text(rule, "ruleContent")) + ")");
        return (Text(suggestion, "destination") == "session" ? "仅本次会话按以下规则允许：" : "保存到此项目的本机设置：") + String.Join("；", rules);
    }
}
}
