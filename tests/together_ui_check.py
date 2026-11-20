"""Collaboration entry points, recovery and mobile layout with the real UI and relay.

    python tests/together_ui_check.py <chrome.exe> <evidence directory>
"""
import json
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import readme_pictures as pictures


class Controlled(pictures.Played):
    failed = ""
    lost_save = False
    failed_target = ""
    models_catalog = {
        "codex": [
            {"id": "gpt-6.2-sol", "label": "GPT-6.2 Sol", "efforts": ["low", "medium", "high", "xhigh", "ultra"], "default_effort": "medium", "is_default": True},
            {"id": "gpt-6.2-astra", "label": "GPT-6.2 Astra", "efforts": ["medium", "high", "xhigh"], "default_effort": "high"},
            {"id": "gpt-5.4", "label": "GPT-5.4", "efforts": ["low", "medium", "high"], "default_effort": "medium"},
            {"id": "gpt-5.3-codex", "label": "GPT-5.3 Codex", "efforts": ["low", "medium", "high"]},
        ],
        "claude": [
            {"id": "claude-opus-4-6", "label": "Claude Opus 4.6", "efforts": ["low", "medium", "high", "max"], "is_default": True},
            {"id": "claude-sonnet-4-6", "label": "Claude Sonnet 4.6", "efforts": ["low", "medium", "high"]},
        ],
        "agy": [{"id": "gemini-2.5-pro", "label": "Gemini 2.5 Pro"}, {"id": "gemini-2.5-flash", "label": "Gemini 2.5 Flash"}],
    }
    team_cards = []
    extra_terminals = []
    extra_session_teams = {}

    def __init__(self, temporary):
        super().__init__(temporary)
        base_overview = self.relay.overview
        base_work = self.relay.work
        played = self

        def overview_with_teams(*args, **kwargs):
            data = base_overview(*args, **kwargs)
            for feat in ("teams-models", "teams-roster", "teams-effort"):
                if feat in played.info.get("features", []) and feat not in data["device"]["features"]:
                    data["device"]["features"] = list(data["device"]["features"]) + [feat]
            if played.team_cards:
                data["device"]["team_cards"] = list(played.team_cards)
            if played.extra_terminals:
                existing = {t["id"] for t in data.get("terminals", [])}
                data["terminals"] = list(data.get("terminals", [])) + [t for t in played.extra_terminals if t["id"] not in existing]
            if played.extra_session_teams:
                data["sessions"] = [dict(s, team=played.extra_session_teams[s["id"]]) if s["id"] in played.extra_session_teams else s for s in data.get("sessions", [])]
            return data

        def work_with_models(payload, *args, **kwargs):
            action = payload.get("action")
            if action == "team_models":
                played.asked.append({"action": "work_team_models"})
                if played.failed == "work_team_models":
                    raise pictures.RemoteError("模型服务暂时不可用")
                return {"models": played.models_catalog}
            if action == "team_start" and isinstance(payload.get("item"), dict) and ("models" in payload["item"] or "roster" in payload["item"] or "auto_roster" in payload["item"]):
                raw_item = dict(payload["item"])
                models = dict(raw_item["models"]) if "models" in raw_item else None
                roster = list(raw_item["roster"]) if "roster" in raw_item else None
                auto_roster = bool(raw_item.get("auto_roster"))
                if roster is not None:
                    now_ms = int(time.time() * 1000)
                    made = {
                        "id": raw_item["id"], "dir": raw_item["dir"], "mode": raw_item["mode"], "goal": raw_item["goal"],
                        "rules": raw_item.get("rules", ""), "state": "running", "note": "", "phase": "",
                        "discuss": bool(raw_item.get("discuss")), "picked": "", "tools": [r["tool"] for r in roster],
                        "roster": roster, "auto_roster": auto_roster, "rounds": raw_item.get("rounds", 3), "round": 1,
                        "created": now_ms, "models": {}, "members": {}, "awaited": ["leader"], "failed": {},
                        "member_sessions": {}, "log": [{"at": now_ms, "who": "目标", "text": raw_item["goal"]}]
                    }
                    played.teams.append(made)
                    played.asked.append({"action": "work_team_start_roster", "item": raw_item})
                    return {"team": made}
                clean = dict(payload, item={k: v for k, v in raw_item.items() if k != "models"})
                res = base_work(clean, *args, **kwargs)
                if res.get("team") and models is not None:
                    res["team"]["models"] = models
                    for one in played.teams:
                        if one["id"] == res["team"]["id"]:
                            one["models"] = models
                played.asked.append({"action": "work_team_start_models", "item": raw_item})
                return res
            if action == "team_say":
                target_team = next((t for t in played.teams if t["id"] == payload.get("team") and t.get("roster")), None)
                if target_team is not None:
                    played.asked.append({"action": "work_team_say_roster", "payload": dict(payload)})
                    target_team["log"].append({"at": int(time.time() * 1000), "who": "user", "text": payload["text"]})
                    return {"team": target_team}
            if action == "team_swap":
                target_team = next((t for t in played.teams if t["id"] == payload.get("team") and t.get("roster")), None)
                if target_team is not None:
                    played.asked.append({"action": "work_team_swap_roster", "payload": dict(payload)})
                    for entry in target_team["roster"]:
                        if entry["role"] == payload.get("role"):
                            entry["tool"] = payload["tool"]
                            entry["model"] = payload.get("model", "")
                            if "effort" in payload:
                                if payload["effort"]:
                                    entry["effort"] = payload["effort"]
                                else:
                                    entry.pop("effort", None)
                    target_team["tools"] = [r["tool"] for r in target_team["roster"]]
                    target_team["log"].append({"at": int(time.time() * 1000), "who": "切换", "text": payload.get("note", "已切换")})
                    return {"team": target_team}
                if "model" in payload:
                    model = payload["model"]
                    clean = {k: v for k, v in payload.items() if k != "model"}
                    res = base_work(clean, *args, **kwargs)
                    if res.get("team"):
                        res["team"].setdefault("models", {})[payload["tool"]] = model
                        for one in played.teams:
                            if one["id"] == res["team"]["id"]:
                                one.setdefault("models", {})[payload["tool"]] = model
                    played.asked.append({"action": "work_team_swap_model", "payload": payload})
                    return res
            return base_work(payload, *args, **kwargs)

        self.relay.overview = overview_with_teams
        self.relay.work = work_with_models

        orig_get = self.server.RequestHandlerClass.do_GET

        def get_with_chat_poll(handler):
            if handler.path.startswith("/api/terminal?terminal="):
                tid = handler.path.split("terminal=", 1)[1].split("&", 1)[0]
                found = next((t for t in played.extra_terminals if t["id"] == tid), None)
                if found:
                    return handler.reply({"terminal": found, "device": {"online": True}, "after": 1, "chunks": [{"seq": 1, "data": json.dumps({"t": "state", "state": "idle", "at": played.now}) + "\n"}]})
            return orig_get(handler)

        self.server.RequestHandlerClass.do_GET = get_with_chat_poll

    def answer(self, op):
        if op["action"] == self.failed:
            raise pictures.RemoteError("测试连接中断")
        if op["action"] == "work_chain_set" and self.lost_save:
            self.lost_save = False
            super().answer(op)
            raise pictures.RemoteError("保存回执暂时不可用，请重试")
        if op["action"] == "session_say" and op.get("session") == self.failed_target:
            raise pictures.RemoteError("目标对话已关闭")
        return super().answer(op)


out = Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="remote-ai-collaboration-", ignore_cleanup_errors=True) as temporary:
    played, browser = Controlled(temporary), pictures.Browser(sys.argv[1], temporary)
    try:
        value, until = browser.value, browser.until
        browser.ask("Page.addScriptToEvaluateOnNewDocument", source="window.__errors=[];addEventListener('error',e=>__errors.push(e.message));addEventListener('unhandledrejection',e=>__errors.push(String(e.reason)));")
        phone = lambda width: dict(width=width, height=844, deviceScaleFactor=1, mobile=True)
        sheet = "document.querySelector('dialog.rs[open] .rs-body')"
        closed = "!document.querySelector('dialog.rs:not(.closing)')"
        # a button of the sheet, by the words it starts with
        press = lambda text: until("(b => b && !b.disabled && (b.click(), true))([...document.querySelectorAll('dialog.rs:not(.closing) button')].find(b => b.innerText.trim().startsWith(%s)))" % json.dumps(text), 'press ' + text)
        typed = lambda name, text: value("(n=>{n.value=%s;n.dispatchEvent(new Event('input',{bubbles:true}));})(document.getElementById(%s))" % (json.dumps(text), json.dumps(name)))
        # a conversation chosen on a page of the sheet, by its title
        picked = lambda title: until("(r => r && (r.click(), true))([...document.querySelectorAll('dialog.rs:not(.closing) .rs-row')].find(r => r.innerText.includes(%s)))" % json.dumps(title), 'pick ' + title)
        open_page = lambda query, ready: browser.open(played.origin + '/?tab=together' + query, ready, query or 'collaboration', **phone(390))
        loaded = "document.querySelectorAll('#chains .card:not(.add)').length===1 && document.querySelectorAll('#schedules .card:not(.add)').length===1"
        opened = lambda name: sheet + " && !!document.getElementById(" + json.dumps(name) + ")"
        title = lambda session: next(one["title"] for one in played.sessions if one["id"] == session)

        # Native rule rows must open the exact stored rule, after async loading finishes.
        open_page('&do=chain&item=' + 'e' * 32, opened('chain-text'))
        assert title(pictures.THESIS) in value("((document.getElementById('chain-from') || {}).innerText || '')")
        assert value("document.getElementById('chain-text').value") == played.chains[0]['text']
        press('审查改动')
        assert value("document.getElementById('chain-text').value").startswith(played.chains[0]['text'] + '\n')
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed by its button')
        open_page('&do=schedule&item=' + 'f' * 32, opened('schedule-text'))
        assert value("document.getElementById('schedule-time').value") == '08:30'
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        played.schedules[0].update(kind='every', minutes=7)
        open_page('&do=schedule&item=' + 'f' * 32, opened('schedule-text'))
        assert value("document.querySelector('.rs-chip[data-value=\"7\"]').getAttribute('aria-pressed')") == 'true', 'Editing must preserve existing custom intervals'
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        played.schedules[0].update(kind='daily', minutes=0)
        open_page('&do=chain&item=' + 'a' * 32, "document.getElementById('toast').textContent.includes('已不存在')")
        assert value(closed), 'A missing rule must not become a new rule'

        # Each list fails independently, with an explicit retry and no false empty state.
        played.failed = 'work_chains'
        open_page('', "document.querySelector('#together .notice')?.textContent.includes('测试连接中断')")
        assert value("document.querySelectorAll('#schedules .card:not(.add)').length") == 1
        assert '读取失败' in value("document.querySelector('#chains').textContent")
        played.failed = ''
        value("document.querySelector('#together .notice button').click()")
        until(loaded, 'retry restores both lists')
        played.failed = 'work_schedules'
        open_page('', "document.querySelector('#together .notice')?.textContent.includes('测试连接中断')")
        assert value("document.querySelectorAll('#chains .card:not(.add)').length") == 1
        played.failed = ''
        value("document.querySelector('#together .notice button').click()")
        until(loaded, 'schedule retry')

        # A lost save acknowledgement followed by a retry must not create two rules.
        played.lost_save = True
        value("document.getElementById('add-chain').click()")
        until(opened('chain-text'), 'new chain')
        value("document.getElementById('chain-from').click()"); picked(title(pictures.THESIS))
        until(opened('chain-text') + " && ((document.getElementById('chain-from') || {}).innerText || '').includes(%s)" % json.dumps(title(pictures.THESIS)), 'source chosen')
        value("document.getElementById('chain-to').click()"); picked(title(pictures.TABLES))
        until(opened('chain-text') + " && ((document.getElementById('chain-to') || {}).innerText || '').includes(%s)" % json.dumps(title(pictures.TABLES)), 'taker chosen')
        typed('chain-text', '检查新的协作入口')
        press('保存')
        until("document.querySelector('dialog.rs .rs-said').textContent.includes('回执')", 'lost save receipt')
        press('保存')
        until(closed + " && document.querySelectorAll('#chains .card:not(.add)').length===2", 'retried save')
        saves = [op for op in played.asked if op['action'] == 'work_chain_set']
        assert len(saves) == 2 and saves[0]['item']['id'] == saves[1]['item']['id']
        assert (saves[1]['item']['from'], saves[1]['item']['to']) == (pictures.THESIS, pictures.TABLES)
        assert len(played.chains) == 2

        # Rapid toggle taps issue one mutation, then show the acknowledged state.
        before = sum(op['action'] == 'work_schedule_set' for op in played.asked)
        value("(b=>{b.click();b.click();})(document.querySelector('#schedules .rs-switch'))")
        until("document.querySelector('#schedules .rs-switch').getAttribute('aria-checked')==='false' && document.querySelector('#schedules .rs-switch').getAttribute('aria-label')==='启用'", 'paused schedule')
        assert sum(op['action'] == 'work_schedule_set' for op in played.asked) == before + 1

        # A new question at a time: the conversation chosen in the sheet, on weekdays.
        value("document.getElementById('add-schedule').click()")
        until(opened('schedule-text'), 'new schedule')
        value("document.getElementById('schedule-session').click()"); picked(title(pictures.TABLES))
        until(opened('schedule-text') + " && ((document.getElementById('schedule-session') || {}).innerText || '').includes(%s)" % json.dumps(title(pictures.TABLES)), 'conversation chosen')
        typed('schedule-text', '汇报一下当前进展')
        press('工作日')
        browser.picture(out / 'schedule-new-390.png')
        press('保存')
        until(closed + " && document.querySelectorAll('#schedules .card:not(.add)').length===2", 'schedule saved')
        made = [op for op in played.asked if op['action'] == 'work_schedule_set'][-1]['item']
        assert (made['session'], made['kind'], made['days'], made['text']) == (pictures.TABLES, 'daily', [1, 2, 3, 4, 5], '汇报一下当前进展'), made

        # Batch failure stays visible. Completing the sheet never resends successful messages.
        played.failed_target = pictures.EXPORT
        value("document.getElementById('ask-many').click()")
        until(opened('many-text'), 'batch question')
        value("document.getElementById('many-%s').click()" % pictures.TABLES)
        until("!!document.getElementById('many-%s')" % pictures.EXPORT, 'the page drawn anew')
        value("document.getElementById('many-%s').click()" % pictures.EXPORT)
        until("document.querySelector('dialog.rs .rs-note').textContent.includes('已选 2')", 'two chosen')
        typed('many-text', '协作测试问题')
        press('发给 2 段对话')
        until("[...document.querySelectorAll('dialog.rs:not(.closing) button')].some(b=>b.innerText.trim()==='完成')", 'partial result')
        assert '已发送 1 / 2' in value("document.querySelector('dialog.rs .rs-note').textContent")
        assert '目标对话已关闭' in value("document.querySelector('dialog.rs').innerText")
        browser.picture(out / 'many-partial-390.png')
        sent = len([op for op in played.asked if op['action'] == 'session_say'])
        press('完成')
        until(closed, 'batch closed')
        assert len([op for op in played.asked if op['action'] == 'session_say']) == sent

        # Keyboard close returns focus to the originating action.
        value("document.getElementById('add-chain').focus();document.getElementById('add-chain').click()")
        until(opened('chain-text'), 'keyboard sheet')
        browser.ask('Input.dispatchKeyEvent', type='keyDown', key='Escape', code='Escape', windowsVirtualKeyCode=27)
        browser.ask('Input.dispatchKeyEvent', type='keyUp', key='Escape', code='Escape', windowsVirtualKeyCode=27)
        until(closed, 'escape closes sheet')
        try:
            until("document.activeElement.id === 'add-chain'", 'focus back on the action')
        except AssertionError:
            raise AssertionError(('focus back on the action', value("[document.activeElement.id, document.activeElement.className, document.activeElement.tagName, document.querySelectorAll('dialog[open]').length]")))

        # ---- 协作任务: only where the computer has it, and runs at least two tools as conversations of the phone's
        assert value("document.getElementById('teams').hidden"), "no teams on a computer without them"
        played.info["features"] = played.info["features"] + ["teams", "teams-v2", "chat", "chat-agy"]
        played.info["tools"] = ["claude", "codex", "agy", "shell"]
        open_page('', "(t => !!t && !t.hidden)(document.getElementById('teams')) && !!document.getElementById('team-new')")
        assert "还没有协作任务" in value("document.getElementById('teams').innerText")
        value("document.getElementById('team-new').click()")
        until(opened('team-goal'), 'the sheet of a new team')
        chips = lambda label: value("[...document.querySelector('dialog.rs[open] [aria-label=%s]').querySelectorAll('.rs-chip')].map(c => c.textContent + (c.getAttribute('aria-pressed') === 'true' ? '*' : ''))" % json.dumps(label))
        assert chips('写的 AI') == ['Claude Code*', 'Codex', 'Antigravity CLI'] and chips('审查的 AI') == ['Claude Code', 'Codex', 'Antigravity CLI*'], chips('审查的 AI')
        press('开始')
        until("document.querySelector('dialog.rs[open]').innerText.includes('请写下要完成什么')", 'a goal is asked for')
        typed('team-goal', '给结算页加上优惠券，并补上测试')
        press('开始')
        until("document.querySelector('dialog.rs[open]').innerText.includes('请选择项目')", 'a project is asked for')
        value("[...document.querySelector('dialog.rs[open] [aria-label=项目]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'web-shop').click()")
        browser.picture(out / 'team-new-390.png')
        press('开始')
        # it begins, and its sheet shows chronological stream, member chips, and bottom composer
        until("!!document.getElementById('member-writer')", "the team's sheet, with its writer")
        made = played.teams[-1]
        assert (made["mode"], made["dir"], made["tools"], made["rounds"], made["goal"]) == ("review", "web-shop", ["claude", "agy"], 3, "给结算页加上优惠券，并补上测试"), made
        assert "进行中" in value("document.querySelector('dialog.rs[open] .rs-who').innerText") and "写手 · Claude Code" in value("document.getElementById('member-writer').innerText")
        assert "给结算页加上优惠券" in value("document.querySelector('dialog.rs[open] .team-stream').innerText")
        # interject mid-run via bottom composer
        typed('team-say-input', '优惠券折扣保留两位小数')
        value("document.getElementById('team-say-send').click()")
        until("document.querySelector('dialog.rs[open] .team-stream').innerText.includes('优惠券折扣保留两位小数')", 'interject appears in chronological stream')
        # verify chronological order (目标 first, user interject last)
        items_txt = value("[...document.querySelectorAll('dialog.rs[open] .team-stream .team-msg')].map(n => n.innerText)")
        assert "目标" in items_txt[0] and "优惠券折扣保留两位小数" in items_txt[-1], items_txt
        # swap active role mid-run via 换 AI 接手
        value("document.getElementById('team-swap').click()")
        until("!!document.getElementById('team-swap-note')", 'swap sub-sheet opened')
        value("[...document.querySelector('dialog.rs[open] [aria-label=\"接手 AI\"]').querySelectorAll('.rs-chip')].find(c => c.textContent.startsWith('Codex')).click()")
        typed('team-swap-note', '接着补测试即可')
        press('交给它继续')
        until("(n => !!n && n.innerText.includes('接着补测试即可'))(document.querySelector('dialog.rs[open] .team-stream'))", 'swap logged and returned to stream')
        assert played.teams[-1]["tools"][0] == "codex", played.teams[-1]
        browser.picture(out / 'team-sheet-390.png')
        press('停止')
        until(closed + " || document.querySelector('dialog.rs[open]').innerText.includes('已停止')", 'it says it stopped')
        assert played.teams[-1]["state"] == "stopped"
        # continue after stopped/done via bottom composer
        typed('team-say-input', '再补上过期优惠券校验')
        value("document.getElementById('team-say-send').click()")
        until("document.querySelector('dialog.rs[open] .rs-who').innerText.includes('进行中') && document.querySelector('dialog.rs[open] .team-stream').innerText.includes('再补上过期优惠券校验')", 'continued into next round after stop')
        assert played.teams[-1]["state"] == "running" and played.teams[-1]["round"] == 2
        press('停止')
        until("document.querySelector('dialog.rs[open]').innerText.includes('已停止')", 'stopped again')
        # 分工协作 + 先讨论再动手 (discuss: true)
        value("document.querySelector('dialog.rs:not(.closing) .rs-close') && document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'the sheet closed')
        open_page('&do=team', opened('team-goal'))
        press('分工协作')
        until("!!document.querySelector('dialog.rs[open] [aria-label=执行成员]')", 'the members')
        assert chips('领队') == ['Claude Code', 'Codex*', 'Antigravity CLI'] and chips('执行成员') == ['Claude Code*', 'Antigravity CLI*'], chips('执行成员')
        value("[...document.querySelector('dialog.rs[open] [aria-label=领队]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'Claude Code').click()")
        until("JSON.stringify([...document.querySelector('dialog.rs[open] [aria-label=执行成员]').querySelectorAll('.rs-chip')].map(c => c.textContent)) === JSON.stringify(['Codex', 'Antigravity CLI'])", 'the leader is no member')
        value("[...document.querySelector('dialog.rs[open] [aria-label=领队]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'Codex').click()")
        until("JSON.stringify([...document.querySelector('dialog.rs[open] [aria-label=执行成员]').querySelectorAll('.rs-chip')].map(c => c.textContent)) === JSON.stringify(['Claude Code', 'Antigravity CLI'])", 'back to Codex leading')
        value("document.getElementById('team-discuss').click()")
        typed('team-goal', '做一个登录页')
        typed('team-rules', 'UI 交给 Gemini，逻辑交给 Claude')
        value("[...document.querySelector('dialog.rs[open] [aria-label=项目]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'thesis').click()")
        press('开始')
        until("!!document.getElementById('member-leader')", "the leader's team")
        assert played.teams[-1]["mode"] == "lead" and played.teams[-1].get("discuss") is True and played.teams[-1]["tools"][0] == "codex" and played.teams[-1]["dir"] == "thesis" and played.teams[-1].get("rules") == "UI 交给 Gemini，逻辑交给 Claude", played.teams[-1]
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed')
        # 并行比稿 (compare): 3 AIs in isolated worktrees -> compare diffs -> merge winner
        open_page('&do=team', opened('team-goal'))
        press('比稿')
        until("!!document.querySelector('dialog.rs[open] [aria-label=\"比稿 AI\"]')", 'compare tools')
        typed('team-goal', '优化商品卡片样式')
        value("[...document.querySelector('dialog.rs[open] [aria-label=项目]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'web-shop').click()")
        press('开始')
        until("!!document.getElementById('team-compare') && !!document.getElementById('team-pick-winner')", 'compare picker rendered')
        assert played.teams[-1]["mode"] == "compare" and len(played.teams[-1]["tools"]) == 3
        value("document.getElementById('compare-tab-codex').click()")
        until("document.querySelector('#team-compare .team-diff').innerText.includes('codex')", 'switched candidate diff tab')
        value("document.getElementById('team-pick-winner').click()")
        until("document.querySelector('dialog.rs[open] .rs-who').innerText.includes('已完成')", 'picked winner merged')
        assert played.teams[-1]["state"] == "done" and played.teams[-1]["picked"] == "codex"
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed')
        until("document.querySelectorAll('#teams .card:not(.add)').length === 3", 'all three on 协作')
        assert "进行中" in value("document.getElementById('teams').innerText") and "1 个进行中 / 3 个" in value("document.getElementById('teams').innerText")
        browser.picture(out / 'teams-390.png')
        # deep link from Android's 协作 list opens that team directly
        open_page('&do=team&item=' + played.teams[1]['id'], "!!document.getElementById('member-leader')")
        assert '做一个登录页' in value("document.querySelector('dialog.rs[open]').innerText")
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed')

        # ---- 协作模型选择 (teams-models): error/retry, role/mode preservation, swap model, detail display
        played.info["features"] = played.info["features"] + ["teams-models"]
        played.failed = "work_team_models"
        open_page('&do=team', opened('team-goal') + " && !!document.getElementById('team-models-retry')")
        assert "模型列表加载失败" in value("document.querySelector('dialog.rs[open]').innerText")
        assert value("document.getElementById('team-model-claude').value") == ""
        played.failed = ""
        value("document.getElementById('team-models-retry').click()")
        until("[...document.getElementById('team-model-claude').options].some(o => o.value === 'claude-opus-4-6')", 'models loaded after retry')
        value("(s => { s.value = 'claude-opus-4-6'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-model-claude'))")
        value("(s => { s.value = 'gemini-2.5-pro'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-model-agy'))")
        # Switch mode from 写审 -> 分工协作: previously chosen models for claude & agy must be preserved, codex stays default
        press('分工协作')
        until("!!document.getElementById('team-model-codex')", 'lead mode models rendered')
        assert value("document.getElementById('team-model-claude').value") == 'claude-opus-4-6', 'claude model preserved across mode switch'
        assert value("document.getElementById('team-model-agy').value") == 'gemini-2.5-pro', 'agy model preserved across mode switch'
        assert value("document.getElementById('team-model-codex').value") == '', 'codex stays default'
        value("(s => { s.value = 'gpt-5.4'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-model-codex'))")
        typed('team-goal', '重构结算页并由领队验收')
        value("[...document.querySelector('dialog.rs[open] [aria-label=项目]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'web-shop').click()")
        value("document.getElementById('team-model-codex').scrollIntoView({block:'center'})")
        browser.picture(out / 'team-models-new-390.png')
        press('开始')
        until("!!document.getElementById('member-leader')", 'modelled lead team opened')
        model_starts = [op for op in played.asked if op['action'] == 'work_team_start_models']
        assert model_starts and model_starts[-1]['item']['models'] == {'codex': 'gpt-5.4', 'claude': 'claude-opus-4-6', 'agy': 'gemini-2.5-pro'}, model_starts
        lead_team = played.teams[-1]
        # Verify detail view shows all planned roles + models (including verify phase and member_sessions fallback)
        leader_tid = "b" * 32
        hist_sid = pictures.uuid(11)
        lead_team["phase"] = "verify"
        lead_team["note"] = "正在核对成员改动与测试结果"
        lead_team["members"] = {"leader": leader_tid, "claude": "c" * 32}
        lead_team["member_sessions"] = {"claude": {"id": hist_sid, "tool": "claude"}}
        lead_team["models"] = {"codex": "gpt-5.4", "claude": "custom-claude-preview", "agy": ""}
        played.said[hist_sid] = [{"role": "user", "text": "实现结算页组件"}, {"role": "assistant", "text": "已完成结算页组件重构"}]
        played.extra_terminals = [
            {"id": leader_tid, "tool": "codex", "dir": "web-shop", "title": "领队对话", "form": "chat", "state": "running", "phase": "busy", "status": "busy", "phase_at": played.now, "created": played.now, "team": lead_team["id"]},
        ]
        played.extra_session_teams = {hist_sid: lead_team["id"]}
        done_team_id = "9" * 32
        confirm_team_id = "8" * 32
        now_sec = int(played.now / 1000)
        played.team_cards = [
            {"id": lead_team["id"], "dir": "web-shop", "goal": lead_team["goal"], "mode": "lead", "state": "running", "phase": "verify", "note": lead_team["note"], "updated": now_sec - 60, "awaited": ["leader"], "tools": ["codex", "claude", "agy"], "models": lead_team["models"]},
            {"id": confirm_team_id, "dir": "web-shop", "goal": "需要人工确认的协作任务", "mode": "review", "state": "running", "phase": "", "note": "等待批准命令", "updated": now_sec - 30, "awaited": ["writer"], "tools": ["claude", "agy"], "models": {}, "confirm": True},
            {"id": done_team_id, "dir": "thesis", "goal": "历史协作任务归档", "mode": "review", "state": "done", "phase": "", "note": "已合并完成", "updated": now_sec - 7200, "awaited": [], "tools": ["claude", "codex"], "models": {}},
        ]
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed')

        # ---- Home (任务) & Project single parent entry + child collapse + Home -> Detail -> Child chat/history -> Back
        work_before_home = sum(1 for op in played.asked if op['action'].startswith('work_'))
        browser.open(played.origin + '/?tab=tasks', "document.querySelectorAll('#active-list .team-card').length === 1 && document.querySelectorAll('#needs-list .team-card').length === 1", 'home with team cards', **phone(390))
        assert sum(1 for op in played.asked if op['action'].startswith('work_')) == work_before_home, 'Home overview must not cause N+1 /api/work calls'
        active_txt = value("document.getElementById('active-list').innerText")
        needs_txt = value("document.getElementById('needs-list').innerText")
        recent_txt = value("document.getElementById('recent-list').innerText")
        assert "重构结算页并由领队验收" in active_txt and "领队验收" in active_txt, active_txt
        assert "领队对话" not in active_txt, "Member terminal must be collapsed into parent team card"
        assert "需要人工确认的协作任务" in needs_txt and "等你确认" in needs_txt, needs_txt
        assert "部署到测试环境" in needs_txt, "Standalone confirm session must remain visible"
        assert "历史协作任务归档" in recent_txt and "已完成" in recent_txt, recent_txt
        assert "给结算页加上优惠券" not in value("document.getElementById('sections').innerText"), "Child historical session must be collapsed"
        browser.picture(out / 'home-teams-390.png')

        # Desktop 1440px home check (no overflow)
        browser.open(played.origin + '/?tab=tasks', "document.querySelectorAll('#active-list .team-card').length === 1", 'home desktop 1440', width=1440, height=900, deviceScaleFactor=1, mobile=False)
        desk_metrics = value("(() => { const p=document.documentElement; return {inner:innerWidth, client:p.clientWidth, scroll:p.scrollWidth, body:document.body.scrollWidth, errors:window.__errors}; })()")
        assert max(desk_metrics['scroll'], desk_metrics['body']) <= desk_metrics['client'] + 1 and not desk_metrics['errors'], desk_metrics
        browser.picture(out / 'home-teams-1440.png')

        # Click parent team card on Home -> opens Team Detail -> see models, verify phase, swap model, open child chat & return, open member_sessions history & return
        browser.open(played.origin + '/?tab=tasks', "document.querySelectorAll('#active-list .team-card').length === 1", 'home before click', **phone(390))
        value("document.querySelector('#active-list .team-card button.open').click()")
        until("!!document.getElementById('member-leader') && document.querySelector('dialog.rs[open]').innerText.includes('领队验收')", 'team detail opened from home card')
        until("document.querySelector('dialog.rs[open]').innerText.includes('GPT-5.4')", 'model catalog display name loaded')
        detail_txt = value("document.querySelector('dialog.rs[open]').innerText")
        assert "领队验收" in detail_txt, detail_txt
        assert "GPT-5.4" in detail_txt and "custom-claude-preview" in detail_txt and "默认" in detail_txt, detail_txt
        browser.picture(out / 'team-verify-detail-390.png')

        # Swap member with model choice
        value("document.getElementById('team-swap').click()")
        until("!!document.getElementById('team-swap-model')", 'swap model selector rendered')
        value("(s => { s.value = 'claude-sonnet-4-6'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-swap-model'))")
        press('交给它继续')
        until("!!document.getElementById('member-leader')", 'returned from swap')
        swap_models = [op for op in played.asked if op['action'] == 'work_team_swap_model']
        assert swap_models and swap_models[-1]['payload'].get('model') == 'claude-sonnet-4-6', swap_models
        # Restore verify phase & leader terminal after swap mock mutated tools/state
        lead_team["phase"] = "verify"
        lead_team["tools"] = ["codex", "claude", "agy"]
        lead_team["members"] = {"leader": leader_tid, "claude": "c" * 32}

        # Open historical member via member_sessions fallback (terminal 'c'*32 is not in terminals) -> back returns to team detail
        open_page('&do=team&item=' + lead_team['id'], "!!document.getElementById('member-claude') && !document.getElementById('member-claude').disabled")
        value("document.getElementById('member-claude').click()")
        until("document.getElementById('sheet').open && document.getElementById('sheet').innerText.includes('已完成结算页组件重构')", 'historical member session opened')
        value("history.back()")
        until("!document.getElementById('sheet').open && !!document.querySelector('dialog.rs[open]')", 'system Back closes only the historical member reader')
        value("document.getElementById('member-claude').click()")
        until("document.getElementById('sheet').open && document.getElementById('sheet').innerText.includes('已完成结算页组件重构')", 'historical member session reopened')
        value("document.querySelector('#sheet .bar button.icon').click()")
        until("!document.getElementById('sheet').open && !!document.querySelector('dialog.rs[open]') && !history.state?.conversationReader", 'reader Back removes its history step and returns to team detail')

        # Open active member chat -> navigate to /chat/?id=...&team=... -> click back -> returns to team detail
        value("document.getElementById('member-leader').click()")
        until("location.pathname.endsWith('/chat/') && new URLSearchParams(location.search).get('team') === %s" % json.dumps(lead_team['id']), 'navigated to member chat with team param')
        until("typeof document.getElementById('back')?.onclick === 'function'", 'chat back handler ready')
        value("document.getElementById('back').click()")
        until("location.search.includes('do=team') && !!document.getElementById('member-leader')", 'back from member chat returned to team detail')
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed after round-trip')

        # Project detail also shows single parent card without child member leakage
        browser.open(played.origin + '/?project=web-shop', "document.querySelectorAll('#p-active .team-card').length === 1", 'project detail with team card', **phone(390))
        assert "领队对话" not in value("document.getElementById('project').innerText"), 'Project detail must collapse child member terminal'
        assert not value("window.__errors.length"), value("window.__errors")

        # ---- 独立执行成员槽位 + 领队工具/模型/思考深度 + 旧端降级 + 切换模型 + 保存后恢复 + 自动组队 (teams-roster / teams-effort)
        # First verify older agent without `teams-effort`: leader area explicitly shows 工具/模型/思考深度, effort disabled at 工具默认 with upgrade hint, and omitted from payload
        played.info["features"] = played.info["features"] + ["teams-roster"]
        open_page('&do=team', opened('team-goal'))
        press('分工协作')
        until("!!document.getElementById('team-roster-effort-leader')", 'leader effort field visible on older agent')
        assert value("document.getElementById('team-roster-effort-leader').disabled") is True, 'effort select disabled without teams-effort'
        assert value("document.getElementById('team-roster-effort-leader').value") == '', 'effort defaults to 工具默认'
        assert "思考深度" in value("document.querySelector('dialog.rs[open]').innerText") and "版本较旧" in value("document.querySelector('dialog.rs[open]').innerText")
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed older-agent sheet')

        # Now enable `teams-effort`: test model switching, effort reconciliation, manual members, detail display, swap, and persistence/restore
        played.info["features"] = played.info["features"] + ["teams-effort"]
        open_page('&do=team', opened('team-goal'))
        press('分工协作')
        until("[...(document.getElementById('team-roster-model-member-1') || {}).options || []].some(o => o.value === 'claude-opus-4-6') && !document.getElementById('team-roster-effort-leader').disabled", 'roster slots, models and leader efforts rendered')
        # Leader defaults to Codex with CLI default model's efforts; select gpt-6.2-sol + ultra, then switch to gpt-5.4 (no ultra -> resets to 工具默认), then gpt-6.2-astra + high
        value("(s => { s.value = 'gpt-6.2-sol'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-model-leader'))")
        until("[...document.getElementById('team-roster-effort-leader').options].some(o => o.value === 'ultra')", 'gpt-6.2-sol offers ultra')
        value("(s => { s.value = 'ultra'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-effort-leader'))")
        assert value("document.getElementById('team-roster-effort-leader').value") == 'ultra'
        value("(s => { s.value = 'gpt-5.4'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-model-leader'))")
        until("![...document.getElementById('team-roster-effort-leader').options].some(o => o.value === 'ultra')", 'switching to gpt-5.4 removes unsupported ultra')
        assert value("document.getElementById('team-roster-effort-leader').value") == '', 'unsupported effort resets to 工具默认 when switching model'
        value("(s => { s.value = 'high'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-effort-leader'))")
        value("(s => { s.value = 'gpt-6.2-astra'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-model-leader'))")
        until("document.getElementById('team-roster-model-leader').value === 'gpt-6.2-astra'", 'switched to gpt-6.2-astra')
        assert value("document.getElementById('team-roster-effort-leader').value") == 'high', 'compatible effort high is preserved across model switch'
        # Switching leader tool to agy (no separate effort tiers) disables effort at 工具默认; switching back to codex restores selectable efforts
        value("(s => { s.value = 'agy'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-tool-leader'))")
        until("document.getElementById('team-roster-effort-leader').disabled", 'agy leader disables effort select at 工具默认')
        assert value("document.getElementById('team-roster-effort-leader').value") == ''
        value("(s => { s.value = 'codex'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-tool-leader'))")
        until("!document.getElementById('team-roster-effort-leader').disabled", 'codex leader re-enables effort select')
        value("(s => { s.value = 'gpt-6.2-sol'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-model-leader'))")
        value("(s => { s.value = 'high'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-effort-leader'))")
        # Select two Claude Code execution members with independent models, plus a third agy execution member
        value("(s => { s.value = 'claude'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-tool-member-1'))")
        value("(s => { s.value = 'claude-opus-4-6'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-model-member-1'))")
        value("(s => { s.value = 'claude'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-tool-member-2'))")
        value("(s => { s.value = 'claude-sonnet-4-6'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-model-member-2'))")
        value("document.getElementById('team-roster-add').click()")
        until("!!document.getElementById('team-roster-tool-member-3') && !document.getElementById('team-roster-add')", 'third member added and capped at 3')
        typed('team-goal', '两位 Claude 分别实现前后端并验收')
        value("[...document.querySelector('dialog.rs[open] [aria-label=项目]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'web-shop').click()")
        press('1 轮')
        browser.picture(out / 'team-lead-manual-390.png')
        press('开始')
        until("!!document.getElementById('member-member-1') && !!document.getElementById('member-member-2')", 'roster team detail opened')
        roster_starts = [op for op in played.asked if op['action'] == 'work_team_start_roster']
        assert roster_starts and roster_starts[-1]['item']['roster'] == [
            {'role': 'leader', 'tool': 'codex', 'model': 'gpt-6.2-sol', 'effort': 'high'},
            {'role': 'member-1', 'tool': 'claude', 'model': 'claude-opus-4-6'},
            {'role': 'member-2', 'tool': 'claude', 'model': 'claude-sonnet-4-6'},
            {'role': 'member-3', 'tool': 'agy', 'model': ''},
        ], roster_starts[-1]['item']
        roster_team = played.teams[-1]
        m1_tid, m2_sid = "d" * 32, pictures.uuid(12)
        roster_team["phase"] = "verify"
        roster_team["last"] = True
        roster_team["note"] = "正在核对执行成员 1 和执行成员 2 的改动"
        roster_team["members"] = {"member-1": m1_tid}
        roster_team["member_sessions"] = {"member-2": {"id": m2_sid, "tool": "claude"}}
        played.said[m2_sid] = [{"role": "assistant", "text": "执行成员 2 已完成后端校验"}]
        played.extra_terminals.append({"id": m1_tid, "tool": "claude", "dir": "web-shop", "title": "执行成员 1 终端", "form": "chat", "state": "running", "phase": "idle", "status": "idle", "phase_at": played.now, "created": played.now, "team": roster_team["id"]})
        open_page('&do=team&item=' + roster_team['id'], "!!document.getElementById('member-member-2') && !!document.getElementById('team-round-steps') && document.getElementById('member-member-1').innerText.includes('Claude Opus 4.6')")
        roster_detail_txt = value("document.querySelector('dialog.rs[open]').innerText")
        assert "领队 · Codex" in roster_detail_txt and "GPT-6.2 Sol" in roster_detail_txt and "高（high）" in roster_detail_txt, roster_detail_txt
        assert "执行成员 1 · Claude Code" in roster_detail_txt and "Claude Opus 4.6" in roster_detail_txt, roster_detail_txt
        assert "执行成员 2 · Claude Code" in roster_detail_txt and "Claude Sonnet 4.6" in roster_detail_txt, roster_detail_txt
        assert "末轮" in value("document.getElementById('team-round-steps').innerText") and "领队验收" in roster_detail_txt, roster_detail_txt
        # Target by role (member-2)
        value("(s => { s.value = 'member-2'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-say-target'))")
        typed('team-say-input', '请补充边界测试')
        value("document.getElementById('team-say-send').click()")
        until("document.querySelector('dialog.rs[open] .team-stream').innerText.includes('请补充边界测试')", 'targeted roster say sent')
        assert [op for op in played.asked if op['action'] == 'work_team_say_roster'][-1]['payload'].get('target') == 'member-2'
        # Swap by role (member-2 -> codex with gpt-5.3-codex)
        value("document.getElementById('team-swap').click()")
        until("!!document.getElementById('team-swap-model')", 'roster swap opened')
        value("[...document.querySelector('dialog.rs[open] [aria-label=\"替换角色\"]').querySelectorAll('.rs-chip')].find(c => c.textContent.startsWith('执行成员 2')).click()")
        value("[...document.querySelector('dialog.rs[open] [aria-label=\"接手 AI\"]').querySelectorAll('.rs-chip')].find(c => c.textContent.startsWith('Codex')).click()")
        until("[...(document.getElementById('team-swap-model') || {}).options || []].some(o => o.value === 'gpt-5.3-codex')", 'swap codex models ready')
        value("(s => { s.value = 'gpt-5.3-codex'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-swap-model'))")
        press('交给它继续')
        until("!!document.getElementById('member-member-2') && document.getElementById('member-member-2').innerText.includes('Codex')", 'roster role swapped')
        swap_roster = [op for op in played.asked if op['action'] == 'work_team_swap_roster'][-1]['payload']
        assert (swap_roster.get('role'), swap_roster.get('tool'), swap_roster.get('model')) == ('member-2', 'codex', 'gpt-5.3-codex'), swap_roster
        # Link on member-2 opens its historical session
        value("document.getElementById('member-member-2').click()")
        until("document.getElementById('sheet').open && document.getElementById('sheet').innerText.includes('执行成员 2 已完成后端校验')", 'member-2 history opened')
        value("document.querySelector('#sheet .bar button.icon').click()")
        until("!document.getElementById('sheet').open && !!document.querySelector('dialog.rs[open]')", 'returned from member-2 history')
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed roster team')

        # Reopen 分工协作 form: saved leader tool/model/effort must be restored cleanly, and 自动组队 preserves leader settings
        open_page('&do=team', opened('team-goal'))
        press('分工协作')
        until("document.getElementById('team-roster-model-leader')?.value === 'gpt-6.2-sol' && document.getElementById('team-roster-effort-leader')?.value === 'high'", 'saved leader tool/model/effort restored')
        press('自动组队')
        until("!document.getElementById('team-roster-tool-member-1') && !document.getElementById('team-discuss') && !!document.getElementById('team-roster-effort-leader')", 'auto roster hides manual members while keeping leader controls')
        assert value("document.getElementById('team-roster-model-leader').value") == 'gpt-6.2-sol'
        assert value("document.getElementById('team-roster-effort-leader').value") == 'high'
        typed('team-goal', '领队自动组队并保留思考深度')
        value("[...document.querySelector('dialog.rs[open] [aria-label=项目]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'web-shop').click()")
        browser.picture(out / 'team-lead-auto-390.png')
        press('开始')
        until("!!document.getElementById('team-auto-roster-status')", 'auto roster team detail opened')
        auto_start_codex = [op for op in played.asked if op['action'] == 'work_team_start_roster'][-1]['item']
        assert auto_start_codex.get('auto_roster') is True and auto_start_codex.get('roster') == [{'role': 'leader', 'tool': 'codex', 'model': 'gpt-6.2-sol', 'effort': 'high'}], auto_start_codex
        assert "GPT-6.2 Sol" in value("document.getElementById('member-leader').innerText") and "高（high）" in value("document.getElementById('member-leader').innerText")
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed codex auto roster team')

        # Auto-roster with only 1 tool installed (claude, when saved codex is absent -> cleanly resets to claude default)
        saved_tools = list(played.info["tools"])
        played.info["tools"] = ["claude", "shell"]
        open_page('&do=team', opened('team-goal') + " && !!document.getElementById('team-roster-tool-leader')")
        press('自动组队')
        until("!document.getElementById('team-roster-tool-member-1') && !document.getElementById('team-discuss')", 'auto roster hides member slots')
        typed('team-goal', '单工具自动组队完成任务')
        value("[...document.querySelector('dialog.rs[open] [aria-label=项目]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'web-shop').click()")
        press('开始')
        until("!!document.getElementById('team-auto-roster-status')", 'auto roster team detail opened')
        auto_start = [op for op in played.asked if op['action'] == 'work_team_start_roster'][-1]['item']
        assert auto_start.get('auto_roster') is True and auto_start.get('roster') == [{'role': 'leader', 'tool': 'claude', 'model': ''}], auto_start
        auto_team = played.teams[-1]
        auto_team["roster"] = [
            {"role": "leader", "tool": "claude", "model": ""},
            {"role": "member-1", "tool": "claude", "model": "claude-sonnet-4-6"}
        ]
        open_page('&do=team&item=' + auto_team['id'], "!!document.getElementById('member-member-1') && document.getElementById('team-auto-roster-status').innerText.includes('已创建 1 个执行成员')")
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed auto roster team')
        played.info["tools"] = saved_tools

        evidence = []
        for width in (320, 390, 430, 1440):
            for state, query, ready in [
                ('overview', '', "document.querySelectorAll('#chains .card:not(.add)').length===2"),
                ('chain', '&do=chain', opened('chain-text')),
                ('team-new', '&do=team', opened('team-goal') + " && !!document.getElementById('team-model-claude')"),
                ('team-lead', '&do=team', opened('team-goal')),
                ('team-detail', '&do=team&item=' + roster_team['id'], "!!document.getElementById('member-member-1') && !!document.getElementById('team-round-steps')")
            ]:
                browser.open(played.origin + '/?tab=together' + query, ready, state, **phone(width))
                if state == 'team-lead':
                    press('分工协作')
                    until("!!document.getElementById('team-roster-effort-leader')", 'lead form ready at width ' + str(width))
                    value("document.getElementById('team-roster-tool-leader').scrollIntoView({block:'center'})")
                time.sleep(.3)
                metrics = value("""(() => { const p=document.documentElement, f=document.querySelector('dialog.rs[open] .rs-body'), box=(f||document.getElementById('together')).getBoundingClientRect();
                  const ids=[...document.querySelectorAll('[id]')].map(n=>n.id); return {inner:innerWidth,client:p.clientWidth,scroll:p.scrollWidth,body:document.body.scrollWidth,
                  left:box.left,right:innerWidth-box.right,form:f?{client:f.clientWidth,scroll:f.scrollWidth}:null,h1:document.querySelectorAll('h1').length,
                  duplicates:ids.filter((id,i)=>ids.indexOf(id)!==i),missingLabels:f?[...f.querySelectorAll('input,textarea,select')].filter(n=>!n.labels.length&&!n.getAttribute('aria-label')).length:0,
                  missingAlt:document.querySelectorAll('img:not([alt])').length,broken:[...document.images].filter(n=>!n.complete||!n.naturalWidth).length,errors:window.__errors}; })()""")
                assert metrics['inner'] == width and metrics['client'] == width and max(metrics['scroll'], metrics['body']) <= width, metrics
                assert abs(metrics['left'] - metrics['right']) <= 2, metrics
                assert not metrics['duplicates'] and not metrics['missingLabels'] and not metrics['errors'], metrics
                if metrics['form']:
                    assert metrics['form']['scroll'] <= metrics['form']['client'], metrics
                browser.picture(out / (state + '-' + str(width) + '.png'))
                evidence.append(dict(width=width, state=state, metrics=metrics))
        browser.ask('Emulation.setEmulatedMedia', features=[dict(name='prefers-reduced-motion', value='reduce')])
        browser.open(played.origin + '/?tab=together&skin=night&do=chain', opened('chain-text'), 'dark reduced motion', **phone(390))
        until("!!document.querySelector('dialog.rs[open]')", 'dark chain')
        browser.picture(out / 'chain-dark-390.png')
        contrast = value("""(() => { const canvas=document.createElement('canvas'), ctx=canvas.getContext('2d');
          const luminance=color=>{ctx.fillStyle=color;ctx.fillRect(0,0,1,1);const rgb=[...ctx.getImageData(0,0,1,1).data].slice(0,3).map(x=>x/255).map(x=>x<=.04045?x/12.92:((x+.055)/1.055)**2.4);return rgb[0]*.2126+rgb[1]*.7152+rgb[2]*.0722;};
          const bg=luminance(getComputedStyle(document.documentElement).getPropertyValue('--bg'));
          return Object.fromEntries(['dialog.rs .rs-note','dialog.rs .rs-chip','dialog.rs .rs-label'].map(sel=>{const n=document.querySelector(sel), fg=luminance(getComputedStyle(n).color);return [sel,(Math.max(fg,bg)+.05)/(Math.min(fg,bg)+.05)];}));})()""")
        assert min(contrast.values()) >= 4.5, contrast
        evidence.append(dict(state='dark-reduced-motion', contrast=contrast))
        (out / 'checks.json').write_text(json.dumps(evidence, indent=2, ensure_ascii=False), encoding='utf-8')
    finally:
        browser.close(); played.close()
print('Collaboration: direct rule editing, partial recovery, stable save IDs, single toggle, a question on weekdays, batch results, keyboard and four viewports passed')
