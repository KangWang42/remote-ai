"""Select one complete Claude permission suggestion without broadening its scope."""


def _rule(value):
    if not isinstance(value, dict) or not isinstance(value.get("toolName"), str) or not value["toolName"]:
        return False
    if "ruleContent" in value and not isinstance(value["ruleContent"], str):
        return False
    if value["toolName"] not in ("Bash", "PowerShell"):
        return True
    content = value.get("ruleContent", "")
    if not content.strip() or any(c in content for c in "\r\n\0"):
        return False
    star = content.find("*")
    # Quoting a glob/regex star does not make it literal in a permission rule.
    return star < 0 or (star == len(content) - 1 and content.count("*") == 1 and star > 1
                        and (content[star - 1].isspace() or content[star - 1] == ":")
                        and bool(content[:star - 1].strip()))


def detail(suggestion):
    rules = "；".join(rule["toolName"] + "(" + (rule.get("ruleContent") or "全部操作") + ")"
                     for rule in suggestion["rules"])
    return ("仅本次会话按以下规则允许：" if suggestion["destination"] == "session"
            else "保存到此项目的本机设置：") + rules


def select(suggestions, allow_session=False):
    if not isinstance(suggestions, list):
        return None
    for suggestion in suggestions:
        if not isinstance(suggestion, dict) or suggestion.get("type") != "addRules" or suggestion.get("behavior") != "allow":
            continue
        if suggestion.get("destination") not in (("localSettings", "session") if allow_session else ("localSettings",)):
            continue
        rules = suggestion.get("rules")
        if not isinstance(rules, list) or not 1 <= len(rules) <= 4 or not all(_rule(rule) for rule in rules):
            continue
        if len(detail(suggestion)) <= 600:
            return suggestion
    return None
