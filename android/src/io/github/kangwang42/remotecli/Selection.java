package io.github.kangwang42.remotecli;

import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.LayerDrawable;
import android.view.Gravity;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * 多选 on 任务: begun from a task's held press, it lasts until 取消 or until the list is left. While it lasts a press on
 * a task chooses it (`toggle`), each task's view wears a mark (`dress`), and a bar takes the tabs' place at the foot
 * (`bar`) with 继续 and 结束 for all that are chosen. What those do is the workbench's; this keeps only the choice.
 */
final class Selection {
    private final Kit kit;
    private final Map<String, AggregateSessions.Thing> chosen = new LinkedHashMap<>();
    private boolean active;
    private TextView count;
    private TextView goOn, end;

    Selection(Kit kit) { this.kit = kit; }

    boolean active() { return active; }
    boolean has(String key) { return chosen.containsKey(key); }
    List<AggregateSessions.Thing> chosen() { return new ArrayList<>(chosen.values()); }

    void start(String key, AggregateSessions.Thing task) {
        active = true;
        chosen.clear();
        if (key != null) chosen.put(key, task);
        said();
    }
    void toggle(String key, AggregateSessions.Thing task) {
        if (chosen.remove(key) == null) chosen.put(key, task);
        said();
    }
    void stop() { active = false; chosen.clear(); count = null; goOn = null; end = null; }

    /** A task's view while choosing: a ring at its top end, filled and ticked when it is chosen; nothing otherwise. */
    void dress(View view, String key) {
        if (!active) { view.setForeground(null); view.setSelected(false); return; }
        boolean on = chosen.containsKey(key);
        GradientDrawable ring = new GradientDrawable();
        ring.setShape(GradientDrawable.OVAL);
        ring.setColor(on ? kit.ACCENT : kit.PANEL);
        ring.setStroke(kit.dp(2), on ? kit.ACCENT : kit.LINE);
        List<Drawable> layers = new ArrayList<>();
        if (on) layers.add(kit.shape(Kit.tint(kit.ACCENT, 18), kit.ACCENT, 18));
        layers.add(ring);
        Drawable tick = null;
        if (on) {
            tick = view.getContext().getDrawable(R.drawable.ic_check);
            if (tick != null) { tick = tick.mutate(); tick.setTint(kit.ON_ACCENT); layers.add(tick); }
        }
        LayerDrawable dressed = new LayerDrawable(layers.toArray(new Drawable[0]));
        int at = on ? 1 : 0, size = kit.dp(22), inset = kit.dp(12);
        dressed.setLayerGravity(at, Gravity.TOP | Gravity.END);
        dressed.setLayerSize(at, size, size);
        dressed.setLayerInsetTop(at, inset); dressed.setLayerInsetEnd(at, inset);
        if (tick != null) {
            dressed.setLayerGravity(at + 1, Gravity.TOP | Gravity.END);
            dressed.setLayerSize(at + 1, kit.dp(14), kit.dp(14));
            dressed.setLayerInsetTop(at + 1, inset + kit.dp(4)); dressed.setLayerInsetEnd(at + 1, inset + kit.dp(4));
        }
        view.setForeground(dressed);
        view.setSelected(on);       // (said so to a screen reader)
    }

    /** The bar at the foot: 取消, how many are chosen, 继续 and 结束. */
    View bar(Runnable cancel, Runnable goOnAll, Runnable endAll) {
        LinearLayout bar = kit.row();
        bar.setPadding(kit.dp(14), kit.dp(10), kit.dp(14), kit.dp(10));
        bar.setBackgroundColor(kit.PANEL);
        kit.elevate(bar, 8);
        TextView quit = kit.medium("取消", 15, kit.MUTED);
        quit.setGravity(Gravity.CENTER); quit.setMinHeight(kit.dp(48)); quit.setPadding(kit.dp(6), 0, kit.dp(10), 0);
        kit.press(quit, cancel);
        bar.addView(quit);
        count = kit.semibold("", 15, kit.INK);
        count.setSingleLine(true);
        bar.addView(count, new LinearLayout.LayoutParams(0, -2, 1));
        goOn = button("继续", kit.ACCENT, kit.ON_ACCENT, goOnAll);
        bar.addView(goOn);
        end = button("结束", kit.badSoft(), kit.BAD, endAll);
        LinearLayout.LayoutParams gap = new LinearLayout.LayoutParams(-2, kit.dp(44));
        gap.leftMargin = kit.dp(8);
        bar.addView(end, gap);
        said();
        return bar;
    }
    private TextView button(String label, int fill, int ink, Runnable action) {
        TextView button = kit.semibold(label, 15, ink);
        button.setGravity(Gravity.CENTER);
        button.setMinHeight(kit.dp(44));
        button.setPadding(kit.dp(18), 0, kit.dp(18), 0);
        button.setBackground(kit.shape(fill, 0, 12));
        kit.press(button, () -> { if (!chosen.isEmpty()) action.run(); });
        return button;
    }
    private void said() {
        if (count == null) return;
        count.setText(chosen.isEmpty() ? "选择对话" : "已选 " + chosen.size() + " 个");
        for (TextView button : new TextView[]{goOn, end}) { button.setEnabled(!chosen.isEmpty()); button.setAlpha(chosen.isEmpty() ? .4f : 1f); }
    }
}
