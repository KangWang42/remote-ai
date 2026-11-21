"""Collaboration entry points, recovery and mobile layout with the real UI and relay.

    python tests/together_ui_check.py <chrome.exe> <evidence directory>
"""
import http.client
import json
import secrets
import sys
import tempfile
import threading
import time
from pathlib import Path
from urllib.parse import quote

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(ROOT / "relay"))
import readme_pictures as pictures
import server as relay_server


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
            {"id": "claude-opus-5-5", "label": "Claude Opus 5.5", "efforts": ["low", "medium", "high", "max"], "default_effort": "high", "is_default": True},
            {"id": "claude-opus-4-6", "label": "Claude Opus 4.6", "efforts": ["low", "medium", "high", "max"]},
            {"id": "claude-sonnet-4-6", "label": "Claude Sonnet 4.6", "efforts": ["low", "medium", "high"]},
        ],
        "agy": [
            {"id": "gemini-pro-orchestrator-neon", "label": "Orchestrator (Neon)", "efforts": ["low", "medium", "high", "xhigh"], "default_effort": "high"},
            {"id": "gemini-4-neon", "label": "Gemini 4 Neon", "efforts": ["low", "medium", "high", "xhigh"], "default_effort": "high", "is_default": True},
            {"id": "gemini-4-argon", "label": "Gemini 4 Argon", "efforts": ["low", "medium", "high", "xhigh"]},
            {"id": "gemini-4-hydrogen", "label": "Gemini 4 Hydrogen", "efforts": ["low", "medium", "high", "xhigh"]},
            {"id": "gemini-2.5-pro", "label": "Gemini 2.5 Pro"},
            {"id": "gemini-2.5-flash", "label": "Gemini 2.5 Flash"},
        ],
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
            for feat in ("teams-models", "teams-roster", "teams-effort", "teams-flex-rounds"):
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
            if action == "team_start" and isinstance(payload.get("item"), dict) and ("models" in payload["item"] or "efforts" in payload["item"] or "roster" in payload["item"] or "auto_roster" in payload["item"]):
                raw_item = dict(payload["item"])
                models = dict(raw_item["models"]) if "models" in raw_item else None
                efforts = dict(raw_item["efforts"]) if "efforts" in raw_item else None
                roster = list(raw_item["roster"]) if "roster" in raw_item else None
                auto_roster = bool(raw_item.get("auto_roster"))
                if roster is not None:
                    if raw_item.get("rounds") == 0:
                        if raw_item.get("mode") != "lead":
                            raise pictures.RemoteError("轮数需在 1 到 10 之间")
                        if "teams-flex-rounds" not in played.info.get("features", []):
                            raise pictures.RemoteError("这台电脑的电脑端版本较旧，不支持自由轮数，请更新电脑端")
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
                clean = dict(payload, item={k: v for k, v in raw_item.items() if k not in ("models", "efforts")})
                res = base_work(clean, *args, **kwargs)
                if res.get("team"):
                    if models is not None:
                        res["team"]["models"] = models
                    if efforts is not None:
                        res["team"]["efforts"] = efforts
                    for one in played.teams:
                        if one["id"] == res["team"]["id"]:
                            if models is not None:
                                one["models"] = models
                            if efforts is not None:
                                one["efforts"] = efforts
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
        value("document.getElementById('team-more-toggle').click()")
        until("!document.getElementById('team-more-panel').hidden && !!document.getElementById('team-discuss')", 'expanded more panel for discuss & rules')
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
        # a task list that cannot be read is no removal: its detail stays open and offers to read again; a task
        # removed elsewhere closes its detail
        live = next(i for i, t in enumerate(played.teams) if t['state'] in ('running', 'paused', 'choose'))
        kept_team = played.teams[live]
        played.failed = 'work_teams'
        browser.open(played.origin + '/?tab=tasks&do=team&item=' + kept_team['id'], "document.querySelector('dialog.rs[open] .team-models-error')?.innerText.includes('测试连接中断')", 'team-unread', **phone(390))
        time.sleep(8)       # two rounds of the detail's reading (every 3.5 s), both failing
        assert value("!!document.querySelector('dialog.rs[open]:not(.closing) .team-models-error')"), 'a failed reading must not close the task as removed'
        assert '协作任务已删除' not in value("document.getElementById('toast').textContent")
        played.failed = ''
        value("document.querySelector('dialog.rs[open] .team-models-retry').click()")
        until("!!document.querySelector('dialog.rs[open]:not(.closing)') && !document.querySelector('dialog.rs[open] .team-models-error') && document.querySelector('dialog.rs[open]').innerText.includes(%s)" % json.dumps(kept_team['goal']), 'read again after the failure')
        played.teams.remove(kept_team)
        until(closed + " && document.getElementById('toast').textContent.includes('协作任务已删除')", 'a task removed elsewhere closes its detail')
        played.teams.insert(live, kept_team)

        # ---- 协作模型选择 (teams-models): error/retry, recommended defaults (Opus 5.5 / Astra 6.2 / Neon), role/mode preservation, swap model, detail display
        played.info["features"] = played.info["features"] + ["teams-models"]
        played.failed = "work_team_models"
        open_page('&do=team', opened('team-goal') + " && !!document.getElementById('team-models-retry')")
        assert "模型列表加载失败" in value("document.querySelector('dialog.rs[open]').innerText")
        assert value("document.getElementById('team-model-claude').value") == ""
        played.failed = ""
        value("document.getElementById('team-models-retry').click()")
        until("document.getElementById('team-model-claude')?.value === 'claude-opus-5-5' && document.getElementById('team-model-agy')?.value === 'gemini-4-neon'", 'recommended models Opus 5.5 and Neon auto-selected after retry')
        value("(s => { s.value = 'claude-opus-4-6'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-model-claude'))")
        value("(s => { s.value = 'gemini-2.5-pro'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-model-agy'))")
        # Switch mode from 写审 -> 分工协作: previously overridden models for claude & agy preserved, codex defaults to recommended Astra 6.2
        press('分工协作')
        until("!!document.getElementById('team-model-codex')", 'lead mode models rendered')
        assert value("document.getElementById('team-model-claude').value") == 'claude-opus-4-6', 'claude model preserved across mode switch'
        assert value("document.getElementById('team-model-agy').value") == 'gemini-2.5-pro', 'agy model preserved across mode switch'
        assert value("document.getElementById('team-model-codex').value") == 'gpt-6.2-astra', 'codex defaults to recommended GPT-6.2 Astra'
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

        # ---- 独立执行成员槽位 + 默认模型与思考深度 (Opus 5.5 / Astra 6.2 / Neon + high) + 设置修改 + 「更多」单次覆盖 + 旧端降级 + 自动组队
        # First verify older agent without `teams-effort`: leader effort in 「更多」 is disabled at 工具默认 with upgrade hint
        played.info["features"] = played.info["features"] + ["teams-roster"]
        open_page('&do=team', opened('team-goal'))
        press('分工协作')
        until("!!document.getElementById('team-more-toggle')", 'lead form with more toggle ready on older agent')
        value("document.getElementById('team-more-toggle').click()")
        until("!document.getElementById('team-more-panel').hidden && !!document.getElementById('team-roster-effort-leader')", 'leader effort field visible in more panel on older agent')
        assert value("document.getElementById('team-roster-effort-leader').disabled") is True, 'effort select disabled without teams-effort'
        assert value("document.getElementById('team-roster-effort-leader').value") == '', 'effort defaults to 工具默认'
        assert "思考深度" in value("document.querySelector('dialog.rs[open]').innerText") and "版本较旧" in value("document.querySelector('dialog.rs[open]').innerText")
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed older-agent sheet')

        # Now enable `teams-effort`: verify Settings defaults (Opus 5.5 / Astra 6.2 / Neon + high), custom save, and reset
        played.info["features"] = played.info["features"] + ["teams-effort"]
        browser.open(played.origin + '/computer/?skin=paper#part-team-defaults', "document.getElementById('default-model-claude')?.value === 'claude-opus-5-5' && document.getElementById('default-model-codex')?.value === 'gpt-6.2-astra' && document.getElementById('default-model-agy')?.value === 'gemini-4-neon'", 'settings default models loaded', **phone(390))
        assert value("document.getElementById('default-effort-claude').value") == 'high'
        assert value("document.getElementById('default-effort-codex').value") == 'high'
        assert value("document.getElementById('default-effort-agy').value") == 'high'
        value("document.getElementById('part-team-defaults').scrollIntoView({block:'start'})")
        time.sleep(.15)
        browser.picture(out / 'settings-team-defaults-light-390.png')
        # Change Claude default in Settings -> persists across reload and applies to new collaboration form
        value("(s => { s.value = 'claude-sonnet-4-6'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('default-model-claude'))")
        until("document.getElementById('default-model-claude').value === 'claude-sonnet-4-6'", 'claude default updated in settings')
        value("(s => { s.value = 'medium'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('default-effort-claude'))")
        until("document.getElementById('default-effort-claude').value === 'medium'", 'claude effort updated in settings')
        browser.open(played.origin + '/computer/?skin=night#part-team-defaults', "document.documentElement.dataset.theme === 'dark' && document.getElementById('default-model-claude')?.value === 'claude-sonnet-4-6' && document.getElementById('default-effort-claude')?.value === 'medium'", 'settings custom defaults persisted in dark mode', **phone(390))
        value("document.getElementById('part-team-defaults').scrollIntoView({block:'start'})")
        time.sleep(.15)
        assert value("document.getElementById('part-team-defaults').getBoundingClientRect().top >= document.querySelector('header.top').getBoundingClientRect().bottom - 2"), 'anchor section must not be obscured by sticky header'
        browser.picture(out / 'settings-team-defaults-dark-390.png')

        # Open new collaboration form: verify strong selected states, Settings defaults applied, and one-shot override in 「更多」 does NOT overwrite Settings
        open_page('&do=team&skin=paper', opened('team-goal'))
        press('分工协作')
        until("!!document.getElementById('team-roster-tool-leader') && document.getElementById('team-more-toggle').innerText.includes('Claude Sonnet 4.6') && document.getElementById('team-more-toggle').innerText.includes('GPT-6.2 Astra')", 'settings defaults reflected on new team form')
        # Verify selected segmented controls (方式: 分工协作, 最多几轮: 3 轮) use high-contrast accent fill rather than faint border
        seg_styles = value("""(() => {
          const acc = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim();
          const activeMode = [...document.querySelectorAll('dialog.rs[open] [aria-label="方式"] button')].find(b => b.getAttribute('aria-pressed') === 'true');
          const activeRound = [...document.querySelectorAll('dialog.rs[open] [aria-label="轮数"] button')].find(b => b.getAttribute('aria-pressed') === 'true');
          return { acc, modeText: activeMode?.textContent, modeBg: activeMode && getComputedStyle(activeMode).backgroundColor, roundText: activeRound?.textContent, roundBg: activeRound && getComputedStyle(activeRound).backgroundColor };
        })()""")
        assert seg_styles["modeText"] == "分工协作" and seg_styles["roundText"] == "3 轮" and seg_styles["modeBg"] == seg_styles["roundBg"] and seg_styles["modeBg"] != "rgba(0, 0, 0, 0)", seg_styles
        time.sleep(.2)
        browser.picture(out / 'team-lead-collapsed-390.png')

        # Expand 「更多」 to test model switching, effort reconciliation, and one-shot overrides
        value("document.getElementById('team-more-toggle').click()")
        until("!document.getElementById('team-more-panel').hidden && !document.getElementById('team-roster-effort-leader').disabled", 'more panel expanded with editable leader & member models')
        assert value("document.getElementById('team-roster-model-leader').value") == 'gpt-6.2-astra'
        assert value("document.getElementById('team-roster-effort-leader').value") == 'high'
        assert value("document.getElementById('team-roster-model-member-1').value") == 'claude-sonnet-4-6'
        assert value("document.getElementById('team-roster-effort-member-1').value") == 'medium'
        # One-shot override: select gpt-6.2-sol + ultra -> switch to gpt-5.4 (no ultra -> resets to 工具默认) -> gpt-6.2-astra + high
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
        # Switching leader tool to agy -> picks Neon + high; selecting gemini-2.5-pro (no effort tiers) disables effort at 工具默认
        value("(s => { s.value = 'agy'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-tool-leader'))")
        until("document.getElementById('team-roster-model-leader').value === 'gemini-4-neon' && document.getElementById('team-roster-effort-leader').value === 'high'", 'agy leader defaults to Neon + high')
        value("(s => { s.value = 'gemini-2.5-pro'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-model-leader'))")
        until("document.getElementById('team-roster-effort-leader').disabled", 'gemini-2.5-pro without effort tiers disables effort select')
        assert value("document.getElementById('team-roster-effort-leader').value") == ''
        value("(s => { s.value = 'codex'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-tool-leader'))")
        until("!document.getElementById('team-roster-effort-leader').disabled", 'codex leader re-enables effort select')
        value("(s => { s.value = 'gpt-6.2-sol'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-model-leader'))")
        value("(s => { s.value = 'high'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-effort-leader'))")
        # Select two Claude Code execution members with independent one-shot models, plus a third agy execution member
        value("(s => { s.value = 'claude'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-tool-member-1'))")
        value("(s => { s.value = 'claude-opus-4-6'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-model-member-1'))")
        value("(s => { s.value = 'high'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-effort-member-1'))")
        value("(s => { s.value = 'claude'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-tool-member-2'))")
        value("(s => { s.value = 'claude-sonnet-4-6'; s.dispatchEvent(new Event('change', {bubbles:true})); })(document.getElementById('team-roster-model-member-2'))")
        value("document.getElementById('team-roster-add').click()")
        until("!!document.getElementById('team-roster-tool-member-3') && !document.getElementById('team-roster-add')", 'third member added and capped at 3')
        typed('team-goal', '两位 Claude 分别实现前后端并验收')
        value("[...document.querySelector('dialog.rs[open] [aria-label=项目]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'web-shop').click()")
        press('1 轮')
        value("document.getElementById('team-more-toggle').scrollIntoView({block:'center'})")
        browser.picture(out / 'team-lead-manual-390.png')
        press('开始')
        until("!!document.getElementById('member-member-1') && !!document.getElementById('member-member-2')", 'roster team detail opened')
        roster_starts = [op for op in played.asked if op['action'] == 'work_team_start_roster']
        assert roster_starts and roster_starts[-1]['item']['roster'] == [
            {'role': 'leader', 'tool': 'codex', 'model': 'gpt-6.2-sol', 'effort': 'high'},
            {'role': 'member-1', 'tool': 'claude', 'model': 'claude-opus-4-6', 'effort': 'high'},
            {'role': 'member-2', 'tool': 'claude', 'model': 'claude-sonnet-4-6', 'effort': 'medium'},
            {'role': 'member-3', 'tool': 'agy', 'model': 'gemini-4-neon', 'effort': 'high'},
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

        # Reopen 分工协作 form: one-shot override (gpt-6.2-sol / claude-opus-4-6) must NOT pollute global Settings defaults (gpt-6.2-astra / claude-sonnet-4-6 + medium)
        open_page('&do=team', opened('team-goal'))
        press('分工协作')
        until("!!document.getElementById('team-more-toggle')", 'more toggle ready after one-shot run')
        value("document.getElementById('team-more-toggle').click()")
        until("document.getElementById('team-roster-model-leader')?.value === 'gpt-6.2-astra' && document.getElementById('team-roster-effort-leader')?.value === 'high' && document.getElementById('team-roster-model-member-1')?.value === 'claude-sonnet-4-6' && document.getElementById('team-roster-effort-member-1')?.value === 'medium'", 'settings defaults untouched by previous one-shot override')
        press('自动组队')
        until("!document.getElementById('team-roster-tool-member-1') && !document.getElementById('team-discuss') && !!document.getElementById('team-roster-effort-leader')", 'auto roster hides manual members while keeping leader controls')
        assert value("document.getElementById('team-roster-model-leader').value") == 'gpt-6.2-astra'
        assert value("document.getElementById('team-roster-effort-leader').value") == 'high'
        typed('team-goal', '领队自动组队并保留思考深度')
        value("[...document.querySelector('dialog.rs[open] [aria-label=项目]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'web-shop').click()")
        browser.picture(out / 'team-lead-auto-390.png')
        press('开始')
        until("!!document.getElementById('team-auto-roster-status')", 'auto roster team detail opened')
        auto_start_codex = [op for op in played.asked if op['action'] == 'work_team_start_roster'][-1]['item']
        assert auto_start_codex.get('auto_roster') is True and auto_start_codex.get('roster') == [{'role': 'leader', 'tool': 'codex', 'model': 'gpt-6.2-astra', 'effort': 'high'}], auto_start_codex
        assert "GPT-6.2 Astra" in value("document.getElementById('member-leader').innerText") and "高（high）" in value("document.getElementById('member-leader').innerText")
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed codex auto roster team')

        # Reset defaults in Settings -> restores recommended Opus 5.5 / Astra 6.2 / Neon + high
        browser.open(played.origin + '/computer/?skin=paper#part-team-defaults', "!!document.getElementById('team-defaults-reset')", 'settings before reset', **phone(390))
        value("document.getElementById('team-defaults-reset').click()")
        until("document.getElementById('default-model-claude')?.value === 'claude-opus-5-5' && document.getElementById('default-effort-claude')?.value === 'high'", 'settings reset restored Opus 5.5 + high')

        # Auto-roster with only 1 tool installed (claude -> uses recommended Claude Opus 5.5 + high)
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
        assert auto_start.get('auto_roster') is True and auto_start.get('roster') == [{'role': 'leader', 'tool': 'claude', 'model': 'claude-opus-5-5', 'effort': 'high'}], auto_start
        auto_team = played.teams[-1]
        auto_team["roster"] = [
            {"role": "leader", "tool": "claude", "model": ""},
            {"role": "member-1", "tool": "claude", "model": "claude-sonnet-4-6"}
        ]
        open_page('&do=team&item=' + auto_team['id'], "!!document.getElementById('member-member-1') && document.getElementById('team-auto-roster-status').innerText.includes('已创建 1 个执行成员')")
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed auto roster team')
        played.info["tools"] = saved_tools

        # ---- Full-screen collaboration detail: stage & members first, preserved reading position on refresh,
        #      running / confirm / ended history member paths, return to originating 任务 page, and 390px light & dark themes
        fs_running_id = "1" * 32
        fs_confirm_id = "2" * 32
        fs_done_id = "3" * 32
        fs_live_tid = "4" * 32
        fs_confirm_tid = "5" * 32
        fs_ended_tid = "6" * 32
        fs_done_sid = pictures.uuid(15)
        long_logs = [{"at": played.now - (12 - i) * 60000, "who": "目标" if i == 0 else ("leader" if i % 2 else "member-1"),
                      "text": ("第 %d 条活动记录：用于检验首屏优先显示阶段与成员状态，以及状态刷新时保持阅读位置不跳动。" % i) + (" 细节内容" * 16)}
                     for i in range(12)]
        long_logs[1]["text"] = "### 本轮分工\n\n- **全屏详情**：优先展示阶段与成员状态\n- **导航排查**：核对 `member_sessions` 与 `returnTab`\n\n```js\nrenderStream('preserve');\n```"
        fs_running = {
            "id": fs_running_id, "dir": "web-shop", "mode": "lead",
            "goal": "全屏协作详情重构并排查成员对话返回路径",
            "rules": "", "state": "running", "note": "",
            "phase": "decision", "discuss": False, "picked": "", "tools": ["codex", "claude", "agy"],
            "roster": [
                {"role": "leader", "tool": "codex", "model": "gpt-6.2-astra", "effort": "high"},
                {"role": "member-1", "tool": "claude", "model": "claude-opus-4-6"},
                {"role": "member-2", "tool": "agy", "model": "gemini-2.5-pro"},
            ],
            "auto_roster": False, "rounds": 3, "round": 1, "created": played.now - 900000,
            "models": {}, "members": {"leader": fs_live_tid, "member-1": "7" * 32},
            "awaited": ["leader"], "failed": {},
            "member_sessions": {"member-2": {"id": fs_done_sid, "tool": "agy"}},
            "log": list(long_logs)
        }
        fs_confirm = {
            "id": fs_confirm_id, "dir": "web-shop", "mode": "review",
            "goal": "待确认状态协作任务测试",
            "rules": "", "state": "running", "note": "写手正在等待命令执行授权",
            "phase": "", "discuss": False, "picked": "", "tools": ["claude", "agy"],
            "rounds": 3, "round": 1, "created": played.now - 300000,
            "models": {"claude": "claude-opus-4-6", "agy": "gemini-2.5-pro"},
            "members": {"writer": fs_confirm_tid}, "awaited": ["writer"], "failed": {},
            "member_sessions": {},
            "log": [
                {"at": played.now - 300000, "who": "目标", "text": "待确认状态协作任务测试"},
                {"at": played.now - 60000, "who": "writer", "text": "准备运行浏览器测试，等待你确认执行命令。"},
            ]
        }
        fs_done = {
            "id": fs_done_id, "dir": "web-shop", "mode": "lead",
            "goal": "已结束协作任务查看历史与返回原任务",
            "rules": "", "state": "done", "note": "领队已通过验收并收尾",
            "phase": "", "discuss": False, "picked": "", "tools": ["codex", "claude", "agy"],
            "roster": [
                {"role": "leader", "tool": "codex", "model": "gpt-6.2-astra", "effort": "high"},
                {"role": "member-1", "tool": "claude", "model": "claude-opus-4-6"},
                {"role": "member-2", "tool": "agy", "model": ""},
            ],
            "auto_roster": False, "rounds": 3, "round": 2, "created": played.now - 3600000,
            "models": {}, "members": {"leader": fs_ended_tid},
            "awaited": [], "failed": {},
            "member_sessions": {"leader": {"id": fs_done_sid, "tool": "codex"}},
            "log": [
                {"at": played.now - 3600000, "who": "目标", "text": "已结束协作任务查看历史与返回原任务"},
                {"at": played.now - 1800000, "who": "leader", "text": "VERIFIED: 全量检查通过，任务结束。"},
            ]
        }
        played.teams.extend([fs_running, fs_confirm, fs_done])
        played.sessions.append({"id": fs_done_sid, "tool": "codex", "dir": "web-shop", "title": "已结束领队历史会话", "live": False, "updated": played.now - 1800000, "team": fs_done_id})
        played.said[fs_done_sid] = [
            {"role": "user", "text": "请检查协作详情返回路径"},
            {"role": "assistant", "text": "已核对成员当前对话、已结束历史与返回原任务路径"}
        ]
        played.extra_terminals.extend([
            {"id": fs_live_tid, "tool": "codex", "dir": "web-shop", "title": "领队实时对话", "form": "chat", "state": "running", "phase": "busy", "status": "busy", "phase_at": played.now, "created": played.now, "team": fs_running_id},
            {"id": fs_confirm_tid, "tool": "claude", "dir": "web-shop", "title": "写手待确认对话", "form": "chat", "state": "running", "phase": "confirm", "status": "confirm", "asks": ["允许运行 python tests/together_ui_check.py 吗？"], "phase_at": played.now, "created": played.now, "team": fs_confirm_id},
            {"id": fs_ended_tid, "tool": "codex", "dir": "web-shop", "title": "已结束领队终端", "form": "chat", "state": "closed", "phase": "ended", "status": "idle", "session": fs_done_sid, "phase_at": played.now - 1800000, "created": played.now - 3600000, "team": fs_done_id},
        ])
        played.team_cards = [
            {"id": fs_confirm_id, "dir": "web-shop", "goal": fs_confirm["goal"], "mode": "review", "state": "running", "phase": "", "note": fs_confirm["note"], "updated": now_sec - 10, "awaited": ["writer"], "tools": ["claude", "agy"], "models": fs_confirm["models"], "confirm": True},
            {"id": fs_running_id, "dir": "web-shop", "goal": fs_running["goal"], "mode": "lead", "state": "running", "phase": fs_running["phase"], "note": fs_running["note"], "updated": now_sec - 20, "awaited": fs_running["awaited"], "tools": ["codex", "claude", "agy"], "roster": fs_running["roster"], "models": {}},
            {"id": fs_done_id, "dir": "web-shop", "goal": fs_done["goal"], "mode": "lead", "state": "done", "phase": "", "note": fs_done["note"], "updated": now_sec - 1200, "awaited": [], "tools": ["codex", "claude", "agy"], "roster": fs_done["roster"], "models": {}},
        ]

        # 1) Open running team from 任务 home (390px light): full-screen, stage & members visible first at scrollTop=0
        browser.open(played.origin + '/?tab=tasks&skin=paper', "document.querySelectorAll('#active-list .team-card').length === 1", 'tasks home before fullscreen team', **phone(390))
        until("document.querySelector('#active-list .team-card .pill')?.textContent === '领队处理中'", 'task card shows leader decision')
        value("document.querySelector('#active-list .team-card[data-team=\"%s\"] button.open').click()" % fs_running_id)
        until("!!document.querySelector('dialog.rs.rs-full[open]') && !!document.getElementById('team-round-steps') && document.querySelectorAll('#team-stream .team-msg').length === 12", 'fullscreen running detail opened')
        until("document.querySelector('dialog.rs.rs-full[open] .rs-badge')?.textContent === '领队处理中' && document.querySelector('#team-round-steps .team-step.active')?.textContent === '领队处理中' && document.getElementById('member-leader')?.innerText.includes('领队处理中')", 'detail shows active decision step and leader')
        assert not value("!!document.querySelector('.team-verify')")
        decision_view = value("""(() => {
          const root = document.documentElement;
          return {inner: innerWidth, client: root.clientWidth, scroll: root.scrollWidth, body: document.body.scrollWidth};
        })()""")
        assert decision_view["inner"] == decision_view["client"] == 390 and max(decision_view["scroll"], decision_view["body"]) <= 391, decision_view
        until("!!document.querySelector('#team-stream .team-msg .body.md h3') && !!document.querySelector('#team-stream .team-msg .body.md strong') && !!document.querySelector('#team-stream .team-msg .body.md code')", 'activity log renders markdown instead of raw syntax')
        md_check = value("(() => { const b = document.querySelectorAll('#team-stream .team-msg')[1].querySelector('.body.md'); return { txt: b ? b.innerText : '', h3: b ? b.querySelectorAll('h3').length : 0, li: b ? b.querySelectorAll('li').length : 0, pre: b ? b.querySelectorAll('pre').length : 0 }; })()")
        assert md_check["h3"] == 1 and md_check["li"] == 2 and md_check["pre"] == 1 and "###" not in md_check["txt"] and "**" not in md_check["txt"], md_check
        fs_box = value("""(() => {
          const dlg = document.querySelector('dialog.rs.rs-full[open]').getBoundingClientRect();
          const body = document.querySelector('dialog.rs.rs-full[open] .rs-body');
          const steps = document.getElementById('team-round-steps').getBoundingClientRect();
          const m1 = document.getElementById('member-leader').getBoundingClientRect();
          const stream = document.getElementById('team-stream').getBoundingClientRect();
          return { w: Math.round(dlg.width), h: Math.round(dlg.height), iw: innerWidth, ih: innerHeight,
            scrollTop: body.scrollTop, scrollHeight: body.scrollHeight, clientHeight: body.clientHeight,
            stepsVisible: steps.top >= 0 && steps.bottom <= innerHeight,
            memberVisible: m1.top >= 0 && m1.bottom <= innerHeight,
            streamBelowMembers: stream.top >= m1.bottom - 1 };
        })()""")
        assert fs_box["w"] == 390 and fs_box["h"] == 844, fs_box
        assert fs_box["scrollTop"] == 0 and fs_box["stepsVisible"] and fs_box["memberVisible"] and fs_box["streamBelowMembers"], fs_box
        assert fs_box["scrollHeight"] > fs_box["clientHeight"] + 150, fs_box
        time.sleep(.22)
        browser.picture(out / 'team-detail-decision-light-390.png')
        # Scroll to the Markdown activity item so visual evidence also confirms rendered Markdown
        value("document.querySelectorAll('#team-stream .team-msg')[1].scrollIntoView({ block: 'start' })")
        browser.picture(out / 'team-detail-markdown-light-390.png')

        # Scroll down to read mid-stream activity, trigger a state + log refresh, verify header updates & reading position stays anchored
        before_anchor = value("""(() => {
          const body = document.querySelector('dialog.rs.rs-full[open] .rs-body');
          const target = document.querySelectorAll('#team-stream .team-msg')[5];
          target.scrollIntoView({ block: 'start' });
          const rect = target.getBoundingClientRect();
          return { key: target.dataset.msgKey, top: rect.top, scrollTop: body.scrollTop };
        })()""")
        assert before_anchor["scrollTop"] > 100 and before_anchor["key"], before_anchor
        fs_running["phase"] = "verify"
        played.team_cards[1]["phase"] = "verify"
        fs_running["awaited"] = ["leader"]
        fs_running["note"] = "领队正在逐项核对全屏详情与返回路径改动，并补充更长的状态说明文本以测试上方高度变化时的阅读保位。"
        fs_running["log"].append({"at": played.now, "who": "leader", "text": "新进展：已进入领队验收阶段，正在核对所有路径。"})
        until("document.querySelector('dialog.rs.rs-full[open] .rs-badge')?.textContent === '领队验收' && document.querySelectorAll('#team-stream .team-msg').length === 13", 'status refresh updated header badge and appended log')
        assert value("document.querySelector('#team-round-steps .team-step.active')?.textContent") == "领队验收"
        after_anchor = value("""((key) => {
          const body = document.querySelector('dialog.rs.rs-full[open] .rs-body');
          const target = [...document.querySelectorAll('#team-stream .team-msg')].find(n => n.dataset.msgKey === key);
          const jump = document.getElementById('team-jump-latest');
          return { top: target ? target.getBoundingClientRect().top : null, scrollTop: body.scrollTop, jumpHidden: jump ? jump.hidden : true, jumpText: jump ? jump.textContent : '' };
        })(%s)""" % json.dumps(before_anchor["key"]))
        assert after_anchor["top"] is not None and abs(after_anchor["top"] - before_anchor["top"]) <= 2.5, (before_anchor, after_anchor)
        assert not after_anchor["jumpHidden"] and "新记录" in after_anchor["jumpText"], after_anchor
        # Click jump-to-latest pill -> reaches newest record and hides pill
        value("document.getElementById('team-jump-latest').click()")
        until("(() => { const b=document.querySelector('dialog.rs.rs-full[open] .rs-body'), j=document.getElementById('team-jump-latest'); return !!b && !!j && j.hidden && (b.scrollHeight - b.scrollTop - b.clientHeight) < 48; })()", 'jump to latest scrolled to bottom and hid pill')

        # Open active member chat from 任务-originated detail -> Back returns to 任务 + same team detail -> closing detail lands back on 任务
        value("document.getElementById('member-leader').click()")
        until("location.pathname.endsWith('/chat/') && new URLSearchParams(location.search).get('team') === %s && new URLSearchParams(location.search).get('returnTab') === 'tasks'" % json.dumps(fs_running_id), 'navigated to member chat preserving returnTab=tasks')
        until("typeof document.getElementById('back')?.onclick === 'function'", 'chat back handler ready for tasks return')
        value("document.getElementById('back').click()")
        until("new URLSearchParams(location.search).get('tab') === 'tasks' && location.search.includes('do=team') && !!document.querySelector('dialog.rs.rs-full[open]') && !!document.getElementById('member-leader')", 'back returned to 任务 with same team detail open')
        value("document.querySelector('dialog.rs.rs-full:not(.closing) .rs-close').click()")
        until(closed + " && document.getElementById('home').dataset.tab === 'tasks' && !document.getElementById('overview').hidden", 'closing team detail returned to originating 任务 list')

        # 2) Pending confirm state (390px light & dark): banner + member chip -> click 去处理 -> chat -> back to 任务 team detail
        value("document.querySelector('#needs-list .team-card[data-team=\"%s\"] button.open').click()" % fs_confirm_id)
        until("!!document.querySelector('dialog.rs.rs-full[open] .team-ask-banner') && document.querySelector('dialog.rs.rs-full[open] .rs-badge')?.textContent === '等你确认'", 'confirm team detail opened')
        assert "等你确认" in value("document.getElementById('member-writer').innerText")
        time.sleep(.22)
        browser.picture(out / 'team-detail-confirm-light-390.png')
        value("document.querySelector('dialog.rs.rs-full[open] .team-ask-banner .go').click()")
        until("location.pathname.endsWith('/chat/') && new URLSearchParams(location.search).get('team') === %s" % json.dumps(fs_confirm_id), 'confirm banner navigated to writer chat')
        until("typeof document.getElementById('back')?.onclick === 'function'", 'confirm chat back ready')
        value("document.getElementById('back').click()")
        until("new URLSearchParams(location.search).get('tab') === 'tasks' && !!document.querySelector('dialog.rs.rs-full[open] .team-ask-banner')", 'back from confirm chat returned to confirm detail on tasks')
        value("document.querySelector('dialog.rs.rs-full:not(.closing) .rs-close').click()")
        until(closed, 'closed confirm team detail')

        # 3) Ended team (state=done) with closed terminal + session history: click member opens history reader (not dead terminal), back returns to detail, close returns to 任务
        value("document.querySelector('#recent-list .team-card[data-team=\"%s\"] button.open').click()" % fs_done_id)
        until("!!document.querySelector('dialog.rs.rs-full[open]') && document.querySelector('dialog.rs.rs-full[open] .rs-badge')?.textContent === '已完成'", 'ended team detail opened')
        assert "查看历史" in value("document.getElementById('member-leader').innerText")
        assert "未参与" in value("document.getElementById('member-member-2').innerText") and value("document.getElementById('member-member-2').disabled") is True
        time.sleep(.22)
        browser.picture(out / 'team-detail-done-light-390.png')
        value("document.getElementById('member-leader').click()")
        until("document.getElementById('sheet').open && document.getElementById('sheet').innerText.includes('已核对成员当前对话')", 'ended member opened history reader instead of closed terminal')
        value("document.querySelector('#sheet .bar button.icon').click()")
        until("!document.getElementById('sheet').open && !!document.querySelector('dialog.rs.rs-full[open]')", 'back from ended member history returned to team detail')
        value("document.querySelector('dialog.rs.rs-full:not(.closing) .rs-close').click()")
        until(closed + " && document.getElementById('home').dataset.tab === 'tasks'", 'closed ended team detail back to 任务')

        # 3.5) Paused state (390px light & dark): full pause reason visible + member 待继续 + bottom 继续 resumes the task
        fs_paused_id = "a" * 32
        fs_paused = {
            "id": fs_paused_id, "dir": "web-shop", "mode": "lead",
            "goal": "核对暂停原因与继续按钮在协作详情中可见",
            "rules": "", "state": "paused",
            "note": "执行成员 1 这一轮提前结束且未给出最终结果：点“继续”接着做，或点“换 AI 接手”",
            "phase": "", "discuss": False, "picked": "", "tools": ["codex", "claude", "agy"],
            "roster": [
                {"role": "leader", "tool": "codex", "model": "gpt-6.2-astra", "effort": "high"},
                {"role": "member-1", "tool": "claude", "model": "claude-opus-5-5", "effort": "high"},
                {"role": "member-2", "tool": "agy", "model": "gemini-4-neon", "effort": "high"},
            ],
            "auto_roster": False, "rounds": 3, "round": 1, "created": played.now - 420000,
            "models": {}, "members": {}, "awaited": ["member-1"], "failed": {},
            "member_sessions": {},
            "log": [
                {"at": played.now - 420000, "who": "目标", "text": "核对暂停原因与继续按钮在协作详情中可见"},
                {"at": played.now - 120000, "who": "member-1", "text": "已启动测试，等待结果返回。"},
            ]
        }
        played.teams.append(fs_paused)
        played.team_cards.append({"id": fs_paused_id, "dir": "web-shop", "goal": fs_paused["goal"], "mode": "lead", "state": "paused", "phase": "", "note": fs_paused["note"], "updated": now_sec - 15, "awaited": ["member-1"], "tools": ["codex", "claude", "agy"], "roster": fs_paused["roster"], "models": {}})
        browser.open(played.origin + '/?tab=tasks&skin=paper&do=team&item=' + fs_paused_id, "!!document.getElementById('team-pause-reason') && !!document.getElementById('team-resume')", 'paused detail light opened', **phone(390))
        paused_box = value("""(() => {
          const reason = document.getElementById('team-pause-reason');
          const resume = document.getElementById('team-resume');
          const m1 = document.getElementById('member-member-1');
          const badge = document.querySelector('dialog.rs.rs-full[open] .rs-badge');
          const rRect = reason.getBoundingClientRect(), bRect = resume.getBoundingClientRect();
          return {
            badge: badge ? badge.textContent : '',
            reasonText: reason.innerText,
            m1Text: m1 ? m1.innerText : '',
            reasonVisible: rRect.top >= 0 && rRect.bottom <= innerHeight,
            resumeVisible: bRect.top >= 0 && bRect.bottom <= innerHeight && !resume.disabled
          };
        })()""")
        assert paused_box["badge"] == "已暂停" and "提前结束" in paused_box["reasonText"] and "待继续" in paused_box["m1Text"], paused_box
        assert paused_box["reasonVisible"] and paused_box["resumeVisible"], paused_box
        time.sleep(.2)
        browser.picture(out / 'team-detail-paused-light-390.png')
        browser.open(played.origin + '/?tab=tasks&skin=night&do=team&item=' + fs_paused_id, "document.documentElement.dataset.theme === 'dark' && !!document.getElementById('team-pause-reason') && !!document.getElementById('team-resume')", 'paused detail dark opened', **phone(390))
        time.sleep(.2)
        browser.picture(out / 'team-detail-paused-dark-390.png')
        # Click 继续 -> resumes task to running, hides pause banner, member returns to 进行中
        value("document.getElementById('team-resume').click()")
        until("document.querySelector('dialog.rs.rs-full[open] .rs-badge')?.textContent === '进行中' && !document.getElementById('team-pause-reason') && document.getElementById('member-member-1')?.innerText.includes('进行中')", 'paused task resumed via 继续 button')
        assert fs_paused["state"] == "running"

        # 3.6) Flexible rounds (`teams-flex-rounds`): older-agent hint vs new-agent `rounds: 0` submission & display
        # Without `teams-flex-rounds`: 自由轮数 is not shown, upgrade hint is visible, and rounds: 0 is never submitted
        open_page('&do=team', opened('team-goal'))
        press('分工协作')
        until("!!document.getElementById('team-rounds-note')", 'older-agent flex-rounds hint rendered')
        assert value("document.getElementById('team-round-flex')") is None, '自由轮数 must stay hidden without teams-flex-rounds'
        old_rounds_note = value("document.getElementById('team-rounds-note').textContent")
        assert "版本较旧" in old_rounds_note and "自由轮数" in old_rounds_note, old_rounds_note
        value("document.querySelector('dialog.rs:not(.closing) .rs-close').click()")
        until(closed, 'closed older-agent flex-rounds sheet')

        # With `teams-flex-rounds`: 写审 stays fixed 1..10, 分工协作 shows 自由轮数 in 3x2 grid, submits rounds: 0, detail/card show current round cleanly
        played.info["features"] = played.info["features"] + ["teams-flex-rounds"]
        open_page('&do=team', opened('team-goal'))
        assert value("document.getElementById('team-round-flex')") is None, 'review mode does not offer 自由轮数'
        press('分工协作')
        until("!!document.getElementById('team-round-flex')", '自由轮数 button visible in lead mode')
        grid_ok = value("""(() => {
          const seg = document.getElementById('team-round-flex').parentElement;
          const btns = [...seg.querySelectorAll('button')];
          return {
            display: getComputedStyle(seg).display,
            overflow: btns.filter(b => b.scrollWidth > b.clientWidth + 1).map(b => b.textContent),
            rows: new Set(btns.map(b => Math.round(b.getBoundingClientRect().top))).size
          };
        })()""")
        assert grid_ok["display"] == "grid" and not grid_ok["overflow"] and grid_ok["rows"] == 2, grid_ok
        value("document.getElementById('team-round-flex').click()")
        until("document.getElementById('team-round-flex').getAttribute('aria-pressed') === 'true'", '自由轮数 selected')
        assert "最多 20 轮" in value("document.getElementById('team-rounds-note').textContent")
        # Switching to 写审 and back must never leak rounds: 0 into 写审
        press('写审')
        until("document.getElementById('team-round-flex') === null && document.querySelector('dialog.rs[open] [aria-label=轮数] [data-rounds=\"3\"]')?.getAttribute('aria-pressed') === 'true'", 'switching to review resets flex rounds to fixed 3')
        press('分工协作')
        until("!!document.getElementById('team-round-flex')", 'back to lead mode with flex rounds')
        value("document.getElementById('team-round-flex').click()")
        until("document.getElementById('team-round-flex').getAttribute('aria-pressed') === 'true'", '自由轮数 re-selected')
        typed('team-goal', '自由轮数协作任务测试：按进展自主推进轮次')
        value("[...document.querySelector('dialog.rs[open] [aria-label=项目]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'web-shop').click()")
        value("document.getElementById('team-round-flex').scrollIntoView({block:'center'})")
        time.sleep(.15)
        browser.picture(out / 'team-flex-rounds-new-390.png')
        press('开始')
        until("!!document.getElementById('team-round-steps') && document.getElementById('team-round-steps').innerText.includes('第 1 轮 · 自由轮数')", 'flex-rounds team detail opened')
        flex_starts = [op for op in played.asked if op['action'] == 'work_team_start_roster']
        assert flex_starts and flex_starts[-1]['item']['rounds'] == 0, flex_starts[-1]
        flex_team = played.teams[-1]
        assert flex_team['rounds'] == 0 and flex_team['round'] == 1, flex_team
        flex_detail_txt = value("document.querySelector('dialog.rs.rs-full[open]').innerText")
        assert "第 1 轮 · 自由轮数" in flex_detail_txt and "/ 0" not in flex_detail_txt and "末轮" not in flex_detail_txt, flex_detail_txt
        # Advance to round 2 verify (still not final) -> shows 第 2 轮 · 自由轮数 without false 末轮
        flex_team["round"] = 2
        flex_team["phase"] = "verify"
        flex_team["note"] = "正在核对第 2 轮改动"
        until("document.getElementById('team-round-steps')?.innerText.includes('第 2 轮 · 自由轮数') && document.querySelector('dialog.rs.rs-full[open] .rs-badge')?.textContent === '领队验收'", 'flex-rounds round 2 verify synced')
        r2_txt = value("document.querySelector('dialog.rs.rs-full[open]').innerText")
        assert "/ 0" not in r2_txt and "末轮" not in r2_txt, r2_txt
        browser.picture(out / 'team-flex-rounds-detail-390.png')
        # When backend marks last=true (round 20 cap), 末轮 appears accurately
        flex_team["round"] = 20
        flex_team["last"] = True
        until("document.getElementById('team-round-steps')?.innerText.includes('第 20 轮 · 自由轮数 · 末轮')", 'flex-rounds final round tag shown when last=true')
        value("document.querySelector('dialog.rs.rs-full:not(.closing) .rs-close').click()")
        until(closed, 'closed flex-rounds detail')
        # Card on 协作 tab also shows 第 20 轮 · 自由轮数 (not / 0)
        until("document.getElementById('teams').innerText.includes('第 20 轮 · 自由轮数') && !document.getElementById('teams').innerText.includes('/ 0')", 'flex-rounds card text accurate')

        # 4) 390px dark theme (skin=night) across running, confirm, and ended team detail + contrast verification
        for name, tid, ready_expr in [
            ('team-detail-running-dark-390.png', fs_running_id, "document.querySelector('dialog.rs.rs-full[open] .rs-badge')?.textContent === '领队验收'"),
            ('team-detail-confirm-dark-390.png', fs_confirm_id, "!!document.querySelector('dialog.rs.rs-full[open] .team-ask-banner')"),
            ('team-detail-done-dark-390.png', fs_done_id, "document.querySelector('dialog.rs.rs-full[open] .rs-badge')?.textContent === '已完成'"),
        ]:
            browser.open(played.origin + '/?tab=tasks&skin=night&do=team&item=' + tid, "!!document.querySelector('dialog.rs.rs-full[open]') && " + ready_expr, name, **phone(390))
            time.sleep(.2)
            dark_metrics = value("""(() => {
              const p=document.documentElement, f=document.querySelector('dialog.rs.rs-full[open] .rs-body');
              return {theme:p.dataset.theme, scroll:Math.max(p.scrollWidth, document.body.scrollWidth), client:p.clientWidth,
                bodyOverflow:f.scrollWidth > f.clientWidth, scrollTop:f.scrollTop, errors:window.__errors};
            })()""")
            assert dark_metrics["theme"] == "dark" and dark_metrics["scroll"] <= 390 and not dark_metrics["bodyOverflow"] and dark_metrics["scrollTop"] == 0 and not dark_metrics["errors"], dark_metrics
            browser.picture(out / name)
        # Restore light skin for subsequent viewport sweep
        browser.open(played.origin + '/?tab=together&skin=paper', "document.documentElement.dataset.theme === 'light'", 'restore light skin', **phone(390))

        evidence = []
        for width in (320, 390, 430, 1440):
            for state, query, ready in [
                ('overview', '', "document.querySelectorAll('#chains .card:not(.add)').length===2"),
                ('chain', '&do=chain', opened('chain-text')),
                ('team-new', '&do=team', opened('team-goal') + " && !!document.getElementById('team-more-toggle')"),
                ('team-lead', '&do=team', opened('team-goal')),
                ('team-detail', '&do=team&item=' + roster_team['id'], "!!document.getElementById('member-member-1') && !!document.getElementById('team-round-steps')")
            ]:
                browser.open(played.origin + '/?tab=together' + query, ready, state, **phone(width))
                if state == 'team-lead':
                    press('分工协作')
                    until("!!document.getElementById('team-more-toggle')", 'lead form ready at width ' + str(width))
                    value("document.getElementById('team-more-toggle').click()")
                    until("!document.getElementById('team-more-panel').hidden && !!document.getElementById('team-roster-effort-leader')", 'lead more panel ready at width ' + str(width))
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


def run_two_session_sync_check(chrome, out):
    """Two independent browser sessions (computer EXE one-time ticket window + phone cookie session)
    talking to the real relay HTTP server (relay/server.py), verifying same task ID, flex rounds (`rounds: 0`),
    round progression, and member progress sync across devices."""
    with tempfile.TemporaryDirectory(prefix="remote-ai-two-session-", ignore_cleanup_errors=True) as temporary:
        password = "sync-" + secrets.token_hex(6)
        srv = relay_server.make_server("127.0.0.1", 0, str(Path(temporary) / "relay-data"), str(ROOT / "web"), password=password)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        port = srv.server_address[1]
        origin = "http://127.0.0.1:%d" % port
        now_ms = int(time.time() * 1000)
        info = {
            "instance": "e" * 32, "enabled": True, "name": "工作站", "version": "1.11.1", "shell": "PowerShell",
            "tools": ["claude", "codex", "agy", "shell"], "workspaces": ["web-shop", "thesis"],
            "features": ["peek", "reach", "end", "work", "chains", "chat", "chat-agy", "teams", "teams-v2",
                         "teams-models", "teams-grouped", "teams-roster", "teams-effort", "teams-flex-rounds"],
        }
        state = {"teams": [], "terminals": [], "sessions": [], "stopped": False}
        state_lock = threading.Lock()

        def cards_payload():
            out_cards = []
            for t in state["teams"]:
                out_cards.append({
                    "id": t["id"], "dir": t["dir"], "goal": t["goal"], "mode": t["mode"],
                    "state": t["state"], "phase": t["phase"], "note": t["note"],
                    "updated": int(time.time()), "awaited": list(t["awaited"]),
                    "tools": list(t["tools"]), "roster": list(t.get("roster") or []),
                    "auto_roster": bool(t.get("auto_roster")), "models": dict(t.get("models") or {}),
                })
            return out_cards

        def push_agent(acks=None):
            with state_lock:
                payload = {"info": dict(info, team_cards=cards_payload()), "terminals": list(state["terminals"]), "sessions": list(state["sessions"])}
            if acks:
                payload["acks"] = acks
            srv.space.relay.agent(payload)

        push_agent()

        def agent_loop():
            while not state["stopped"]:
                try:
                    ops = srv.space.relay.pull({"instance": info["instance"], "wait": 1})["operations"]
                except Exception:
                    time.sleep(0.1)
                    continue
                if not ops:
                    continue
                acks = []
                for op in ops:
                    act = op.get("action")
                    try:
                        if act == "work_chains":
                            acks.append({"id": op["id"], "error": "", "result": {"chains": []}})
                        elif act == "work_schedules":
                            acks.append({"id": op["id"], "error": "", "result": {"schedules": []}})
                        elif act == "work_team_models":
                            acks.append({"id": op["id"], "error": "", "result": {"models": Controlled.models_catalog}})
                        elif act == "work_teams":
                            with state_lock:
                                acks.append({"id": op["id"], "error": "", "result": {"teams": [json.loads(json.dumps(t)) for t in state["teams"]]}})
                        elif act == "work_team_start":
                            item = op["item"]
                            m1_tid = "1" * 32
                            made = {
                                "id": item["id"], "dir": item["dir"], "mode": item["mode"], "goal": item["goal"],
                                "rules": item.get("rules", ""), "state": "running", "note": "执行成员 1 正在实现功能",
                                "phase": "", "discuss": bool(item.get("discuss")), "picked": "",
                                "tools": list(item["tools"]), "roster": list(item.get("roster") or []),
                                "auto_roster": bool(item.get("auto_roster")), "rounds": item["rounds"], "round": 1,
                                "created": int(time.time() * 1000), "models": dict(item.get("models") or {}),
                                "efforts": dict(item.get("efforts") or {}),
                                "members": {"member-1": m1_tid}, "awaited": ["member-1"], "failed": {},
                                "member_sessions": {},
                                "log": [
                                    {"at": int(time.time() * 1000), "who": "目标", "text": item["goal"]},
                                    {"at": int(time.time() * 1000), "who": "leader", "text": "第 1 轮已分派给执行成员 1 实现并自测。"},
                                ],
                            }
                            with state_lock:
                                state["teams"].insert(0, made)
                                state["terminals"].append({
                                    "id": m1_tid, "tool": "claude", "dir": item["dir"], "title": "执行成员 1 对话",
                                    "form": "chat", "state": "running", "phase": "busy", "status": "busy",
                                    "phase_at": now_ms, "created": now_ms, "team": item["id"],
                                })
                            acks.append({"id": op["id"], "error": "", "result": {"team": json.loads(json.dumps(made))}})
                        else:
                            acks.append({"id": op["id"], "error": "", "result": {}})
                    except Exception as exc:
                        acks.append({"id": op["id"], "error": str(exc)})
                push_agent(acks)

        threading.Thread(target=agent_loop, daemon=True).start()

        def http_post(path, payload, token=""):
            conn = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
            headers = {"Content-Type": "application/json"}
            if token:
                headers["Authorization"] = "Bearer " + token
            conn.request("POST", path, json.dumps(payload).encode("utf-8"), headers)
            resp = conn.getresponse()
            body = json.loads(resp.read().decode("utf-8"))
            status = resp.status
            conn.close()
            return status, body

        pc_dir = Path(temporary) / "pc-profile"
        phone_dir = Path(temporary) / "phone-profile"
        pc_dir.mkdir(); phone_dir.mkdir()
        pc = pictures.Browser(chrome, str(pc_dir))
        phone_browser = pictures.Browser(chrome, str(phone_dir))
        try:
            # 1) Computer EXE flow: POST /api/login -> POST /api/ticket -> open /?tab=together&do=team#t=<ticket>
            st, login_body = http_post("/api/login", {"password": password})
            assert st == 200 and login_body.get("token"), (st, login_body)
            st, ticket_body = http_post("/api/ticket", {}, token=login_body["token"])
            assert st == 200 and ticket_body.get("ticket"), (st, ticket_body)
            ticket = ticket_body["ticket"]
            exe_url = origin + "/?tab=together&do=team#t=" + quote(ticket, safe="")

            pc.open(exe_url, "!!document.querySelector('dialog.rs[open] #team-goal')", "computer window opens new team sheet via one-time ticket", width=480, height=880, deviceScaleFactor=1, mobile=True)
            assert pc.value("location.hash") == "", "ticket hash must be stripped from URL"
            # Reusing the same one-time ticket must be rejected by relay
            st2, _ = http_post("/api/login", {"ticket": ticket})
            assert st2 == 401, ("one-time ticket should fail on second use", st2)

            # Computer selects 分工协作 + 自由轮数 (rounds: 0), submits task
            pc.until("(b => b && (b.click(), true))([...document.querySelectorAll('dialog.rs[open] button')].find(b => b.innerText.trim() === '分工协作'))", "pc choose lead")
            pc.until("!!document.getElementById('team-round-flex')", "pc flex rounds visible")
            pc.value("document.getElementById('team-round-flex').click()")
            pc.until("document.getElementById('team-round-flex').getAttribute('aria-pressed') === 'true'", "pc flex rounds selected")
            pc.value("(n => { n.value = '电脑端发起的自由轮数协作任务'; n.dispatchEvent(new Event('input', {bubbles:true})); })(document.getElementById('team-goal'))")
            pc.value("[...document.querySelector('dialog.rs[open] [aria-label=项目]').querySelectorAll('.rs-chip')].find(c => c.textContent === 'web-shop').click()")
            pc.value("document.getElementById('team-start').click()")
            pc.until("!!document.getElementById('team-round-steps') && document.getElementById('team-round-steps').innerText.includes('第 1 轮 · 自由轮数') && document.getElementById('member-member-1')?.innerText.includes('进行中')", "pc sees created team detail with round 1 and member-1 progress")

            with state_lock:
                assert len(state["teams"]) == 1, state["teams"]
                created_team = state["teams"][0]
                team_id = created_team["id"]
                assert created_team["rounds"] == 0 and created_team["round"] == 1, created_team

            # 2) Independent phone session (separate profile/cookies): must be signed out first, then sign in and see exact same task ID, round & member progress
            phone_browser.open(origin + "/?tab=tasks", "!document.getElementById('login').hidden", "phone starts signed out in isolated session", width=390, height=844, deviceScaleFactor=1, mobile=True)
            phone_browser.value("(p => { p.value = %s; p.dispatchEvent(new Event('input', {bubbles:true})); })(document.getElementById('password'))" % json.dumps(password))
            phone_browser.value("document.querySelector('#login button[type=submit]').click()")
            phone_browser.until("!!document.querySelector('#active-list .team-card[data-team=\"%s\"]')" % team_id, "phone sees exact same team_id card on tasks tab")
            phone_browser.value("document.querySelector('#active-list .team-card[data-team=\"%s\"] button.open').click()" % team_id)
            phone_browser.until("!!document.getElementById('team-round-steps') && document.getElementById('team-round-steps').innerText.includes('第 1 轮 · 自由轮数') && document.getElementById('member-member-1')?.innerText.includes('进行中')", "phone detail synced same round 1 and member-1 status")

            # 3) Advance backend to Round 2 + 领队验收: both computer and phone independently poll and sync to Round 2 & updated member status
            with state_lock:
                t = state["teams"][0]
                t["round"] = 2
                t["phase"] = "verify"
                t["awaited"] = ["leader"]
                t["note"] = "执行成员 1 已交付，领队正在第 2 轮验收"
                t["log"].append({"at": int(time.time() * 1000), "who": "member-1", "text": "已完成实现并通过本地测试。"})
            push_agent()

            pc.until("document.getElementById('team-round-steps')?.innerText.includes('第 2 轮 · 自由轮数') && document.querySelector('dialog.rs.rs-full[open] .rs-badge')?.textContent === '领队验收' && document.getElementById('member-leader')?.innerText.includes('验收中')", "computer window synced round 2 verify")
            phone_browser.until("document.getElementById('team-round-steps')?.innerText.includes('第 2 轮 · 自由轮数') && document.querySelector('dialog.rs.rs-full[open] .rs-badge')?.textContent === '领队验收' && document.getElementById('member-leader')?.innerText.includes('验收中')", "phone synced round 2 verify")
            assert "/ 0" not in pc.value("document.querySelector('dialog.rs.rs-full[open]').innerText")
            assert "/ 0" not in phone_browser.value("document.querySelector('dialog.rs.rs-full[open]').innerText")
            pc.picture(out / "sync-computer-480.png")
            phone_browser.picture(out / "sync-phone-390.png")

            # Phone 协作 tab also shows 第 2 轮 · 自由轮数 on the card
            phone_browser.open(origin + "/?tab=together", "document.getElementById('teams')?.innerText.includes('第 2 轮 · 自由轮数')", "phone together tab card shows round 2 flex rounds", width=390, height=844, deviceScaleFactor=1, mobile=True)
            assert "/ 0" not in phone_browser.value("document.getElementById('teams').innerText")
        finally:
            state["stopped"] = True
            pc.close(); phone_browser.close()
            srv.shutdown(); srv.server_close()


run_two_session_sync_check(sys.argv[1], out)
print('Collaboration: flex rounds, older-agent hint, two-session sync, direct rule editing, partial recovery, stable save IDs, single toggle, a question on weekdays, batch results, keyboard and four viewports passed')
