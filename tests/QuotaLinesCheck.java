package io.github.kangwang42.remotecli;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/** The limits the app shows on 我的 (QuotaLines): the newest of each tool across computers, in the page's words. */
public final class QuotaLinesCheck {
    static void require(boolean condition, String message) { if (!condition) throw new AssertionError(message); }
    public static void main(String[] args) {
        long now = 1_800_000_000L;
        QuotaLines.Reading older = new QuotaLines.Reading("claude", 1000, "", Arrays.asList(new QuotaLines.Window("five_hour", 10, now + 3600)));
        QuotaLines.Reading newer = new QuotaLines.Reading("claude", 2000, "", Arrays.asList(new QuotaLines.Window("five_hour", 38, now + 2 * 3600), new QuotaLines.Window("seven_day", 93.4, now - 5)));
        QuotaLines.Reading codex = new QuotaLines.Reading("codex", 1500, "Codex 用 ChatGPT 账号登录后才能读取额度", Collections.<QuotaLines.Window>emptyList());
        QuotaLines.Reading quiet = new QuotaLines.Reading("agy", 1500, "", Collections.<QuotaLines.Window>emptyList());
        List<QuotaLines.Reading> shown = QuotaLines.newest(Arrays.asList(codex, older, quiet, newer), now);
        require(shown.size() == 2 && shown.get(0).tool.equals("claude") && shown.get(1).tool.equals("codex"), "claude first, codex with why, a tool with nothing to say left out");
        require(shown.get(0).at == 2000 && shown.get(0).windows.size() == 1, "the newest of two computers, its window that is over left out");
        QuotaLines.Window w = shown.get(0).windows.get(0);
        require(QuotaLines.label("claude", w).equals("Claude Code · 5 小时"), QuotaLines.label("claude", w));
        require(QuotaLines.detail(w, now).equals("38% · 2 小时后重置"), QuotaLines.detail(w, now));
        require(QuotaLines.detail(new QuotaLines.Window("seven_day", 93.4, now + 5 * 86400), now).equals("93% · 5 天后重置") && QuotaLines.high(new QuotaLines.Window("seven_day", 93.4, 0)), "nearly used up");
        require(QuotaLines.detail(new QuotaLines.Window("five_hour", 5, now + 600), now).equals("5% · 10 分钟后重置"), "minutes");
        require(QuotaLines.window("300m").equals("5 小时") && QuotaLines.window("10080m").equals("7 天") && QuotaLines.window("primary").equals("primary"), "windows named by their length");
        require(QuotaLines.detail(new QuotaLines.Window("x", 12.4, 0), now).equals("12%"), "without a time");
        QuotaLines.Reading agy = new QuotaLines.Reading("agy", 1800, "", Arrays.asList(
            new QuotaLines.Window("five_hour", 2, now + 3600),
            new QuotaLines.Window("seven_day", 1, now + 86400),
            new QuotaLines.Window("3p_five_hour", 12, now + 3600, "Claude / GPT"),
            new QuotaLines.Window("3p_seven_day", 91, now + 86400, "Claude / GPT"),
            new QuotaLines.Window("gemini_argon_wtu_seven_day", 5, now + 86400, "Gemini 4 Argon")));
        List<QuotaLines.Window> primary = QuotaLines.primary(agy), extra = QuotaLines.extra(agy);
        require(primary.size() == 2 && extra.size() == 3 && QuotaLines.extraPoolCount(extra) == 2 && QuotaLines.anyHigh(extra), "agy primary vs secondary pools");
        require(QuotaLines.label("agy", primary.get(0)).equals("Antigravity CLI · 5 小时"), QuotaLines.label("agy", primary.get(0)));
        require(QuotaLines.label("agy", extra.get(0)).equals("Claude / GPT · 5 小时"), QuotaLines.label("agy", extra.get(0)));
        require(QuotaLines.label("agy", extra.get(2)).equals("Gemini 4 Argon · 本周"), QuotaLines.label("agy", extra.get(2)));
        System.out.println("quota lines: the newest of each tool, in the page's words");
    }
}
