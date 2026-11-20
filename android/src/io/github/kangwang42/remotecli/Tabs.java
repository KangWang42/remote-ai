package io.github.kangwang42.remotecli;

import android.graphics.Rect;
import android.graphics.Typeface;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Build;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.View;
import android.view.ViewTreeObserver;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.util.function.Consumer;

/**
 * The floating pill dock at the foot of the app's own lists: 任务, 项目, 协作, 我的. Matches the floating
 * navigation bar on the embedded pages (web/index.html, "#tabs" in web/app.css), so that the foot of the
 * screen stays cohesive between the app's lists and a computer's pages.
 */
final class Tabs {
    /** A tab by the name of its screen, and by the name a computer's pages call it. */
    static final String[] KEYS = {"home", "projects", "together", "me"}, NAMES = {"tasks", "projects", "together", "me"};
    private static final String[] LABELS = {"任务", "项目", "协作", "我的"};
    private static final int[] ICONS = {R.drawable.ic_tab_tasks, R.drawable.ic_folder, R.drawable.ic_tab_together, R.drawable.ic_tab_me};

    static boolean has(String key) { return at(KEYS, key) >= 0; }
    /** The screen of the tab a page names, or null for a name that is none. */
    static String key(String named) { int at = Math.max(at(NAMES, named), at(KEYS, named)); return at < 0 ? null : KEYS[at]; }
    private static int at(String[] list, String one) { for (int i = 0; i < list.length; i++) if (list[i].equals(one)) return i; return -1; }

    private final MainActivity activity;
    private final Kit kit;
    private TextView count;
    private int waiting;

    Tabs(MainActivity activity, Kit kit) { this.activity = activity; this.kit = kit; }

    /** The size the pages give their text ("html" in web/app.css and what is chosen as the text's size), in dp. */
    private float root() {
        String chosen = activity.prefs.getString("look-text", "normal");
        return "small".equals(chosen) ? 14f : "large".equals(chosen) ? 17f : "larger".equals(chosen) ? 19f : 15.5f;
    }

    View build(String current, Consumer<String> picked) {
        LinearLayout bar = kit.column();
        bar.setBackgroundColor(kit.BG);
        bar.setClipChildren(false);
        bar.setClipToPadding(false);
        bar.setPadding(kit.dp(14), kit.dp(6), kit.dp(14), kit.dp(12));

        LinearLayout inner = new LinearLayout(activity);
        inner.setPadding(kit.dp(6), kit.dp(6), kit.dp(6), kit.dp(6));
        int dockFill = Kit.light(kit.BG) ? Kit.tint(kit.PANEL, 242) : kit.PANEL;
        inner.setBackground(kit.shape(dockFill, kit.rim(), 24));
        kit.elevate(inner, 6f);

        for (int i = 0; i < KEYS.length; i++) {
            final String key = KEYS[i];
            boolean on = key.equals(current);
            int shade = on ? kit.accentInk() : kit.MUTED;
            FrameLayout tab = new FrameLayout(activity);
            tab.setMinimumHeight(kit.dp(52));
            StateListDrawable bg = new StateListDrawable();
            bg.addState(new int[]{android.R.attr.state_pressed}, kit.shape(on ? kit.accentSoft() : kit.RAISED, 0, 18));
            bg.addState(new int[]{}, on ? kit.shape(kit.accentSoft(), 0, 18) : new ColorDrawable(0));
            tab.setBackground(bg);

            LinearLayout both = kit.column();
            both.setGravity(Gravity.CENTER_HORIZONTAL);
            both.addView(kit.icon(ICONS[i], shade, 22), new LinearLayout.LayoutParams(kit.dp(22), kit.dp(22)));
            TextView label = kit.text(LABELS[i], 11, shade);
            label.setTextSize(TypedValue.COMPLEX_UNIT_SP, .7f * root());
            label.setTypeface(Build.VERSION.SDK_INT >= 28 ? Typeface.create(Typeface.DEFAULT, on ? 600 : 500, false) : (on ? Typeface.DEFAULT_BOLD : Typeface.DEFAULT));
            label.setIncludeFontPadding(false); label.setLineSpacing(0, 1f); label.setSingleLine(true);
            LinearLayout.LayoutParams under = new LinearLayout.LayoutParams(-2, -2);
            under.topMargin = kit.dp(3);
            both.addView(label, under);
            tab.addView(both, new FrameLayout.LayoutParams(-2, -2, Gravity.CENTER));

            if (i == 0) {
                count = kit.bold("", 10, 0xffffffff);
                count.setTextSize(TypedValue.COMPLEX_UNIT_SP, .64f * root());
                count.setGravity(Gravity.CENTER); count.setIncludeFontPadding(false); count.setSingleLine(true);
                count.setMinWidth(kit.dp(17)); count.setMinHeight(kit.dp(17)); count.setPadding(kit.dp(4), 0, kit.dp(4), 0);
                GradientDrawable badgeBg = kit.shape(kit.BAD, 0, 9);
                badgeBg.setStroke(kit.dp(1.5f), on ? kit.accentSoft() : kit.PANEL);
                count.setBackground(badgeBg);
                FrameLayout.LayoutParams above = new FrameLayout.LayoutParams(-2, kit.dp(17), Gravity.TOP | Gravity.CENTER_HORIZONTAL);
                above.topMargin = kit.dp(3); above.leftMargin = kit.dp(14);
                tab.addView(count, above);
                count(waiting);
            }
            tab.setClickable(true); tab.setFocusable(true);
            tab.setSelected(on);
            tab.setContentDescription(LABELS[i] + (on ? "，已选中" : ""));
            tab.setOnClickListener(pressedTab -> { kit.haptic(pressedTab, HapticFeedbackConstants.CLOCK_TICK); picked.accept(key); });
            LinearLayout.LayoutParams tabLp = new LinearLayout.LayoutParams(0, -2, 1);
            if (i > 0) tabLp.leftMargin = kit.dp(4);
            inner.addView(tab, tabLp);
        }
        int maxDock = Math.min(activity.getResources().getDisplayMetrics().widthPixels - kit.dp(28), kit.dp(540));
        LinearLayout.LayoutParams middle = new LinearLayout.LayoutParams(maxDock, -2);
        middle.gravity = Gravity.CENTER_HORIZONTAL;
        bar.addView(inner, middle);
        yieldToKeyboard(bar);
        return bar;
    }

    /** How many things wait for an answer, on every computer: shown on 任务 whichever tab is open. */
    void count(int waiting) {
        this.waiting = waiting;
        if (count == null) return;
        count.setText(waiting > 9 ? "9+" : String.valueOf(waiting));
        count.setVisibility(waiting > 0 ? View.VISIBLE : View.GONE);
    }

    /** The keyboard takes the foot of the screen while something is typed: the tabs are away for as long. */
    private void yieldToKeyboard(final View bar) {
        final ViewTreeObserver.OnGlobalLayoutListener watch = () -> {
            Rect seen = new Rect();
            bar.getWindowVisibleDisplayFrame(seen);
            boolean typing = bar.getRootView().getHeight() - seen.bottom > kit.dp(150);
            if (typing == (bar.getVisibility() == View.VISIBLE)) bar.setVisibility(typing ? View.GONE : View.VISIBLE);
        };
        bar.addOnAttachStateChangeListener(new View.OnAttachStateChangeListener() {
            @Override public void onViewAttachedToWindow(View view) { view.getViewTreeObserver().addOnGlobalLayoutListener(watch); }
            @Override public void onViewDetachedFromWindow(View view) { view.getViewTreeObserver().removeOnGlobalLayoutListener(watch); }
        });
    }
}
