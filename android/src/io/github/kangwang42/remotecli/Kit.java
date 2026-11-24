package io.github.kangwang42.remotecli;

import android.app.Activity;
import android.app.Dialog;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.os.Build;
import android.text.TextUtils;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.View;
import android.view.Window;
import android.view.animation.DecelerateInterpolator;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.util.function.IntConsumer;

/** The colours and small views the app's own screens are built from, matching the pages a computer serves. */
final class Kit {
    private static final int[][] SKINS = Palettes.COLORS;
    static final String[] NAMES = Palettes.NAMES;
    static final String[] TITLES = Palettes.TITLES;
    /** The background of the skin with this name; an unknown name gives the default. */
    static int shade(String name) {
        for (int i = 0; i < NAMES.length; i++) if (NAMES[i].equals(name)) return 0xff000000 | SKINS[i][0];
        return PAPER;
    }
    static String title(String name) {
        for (int i = 0; i < NAMES.length; i++) if (NAMES[i].equals(name)) return TITLES[i];
        return TITLES[4];
    }
    /** The name the pages give the skin with this background, or nothing for another colour. */
    static String name(int background) {
        for (int i = 0; i < SKINS.length; i++) if (SKINS[i][0] == (background & 0xffffff)) return NAMES[i];
        return "";
    }
    /** The skin a new installation starts with. */
    static final int PAPER = 0xfff2f3f7;
    private final Activity activity;
    int BG, PANEL, RAISED, LINE, INK, MUTED, ACCENT, ON_ACCENT, GOOD, BUSY, BAD;
    private int SUB, CARD_BORDER, DIVIDER, ACCENT_SOFT, ACCENT_INK, BUSY_SOFT, BUSY_INK, BUSY_DOT, GOOD_SOFT, GOOD_INK, GOOD_DOT, BAD_SOFT;

    Kit(Activity activity) { this.activity = activity; }

    static boolean light(int shade) { return (Color.red(shade) * 299 + Color.green(shade) * 587 + Color.blue(shade) * 114) / 1000 > 150; }
    /** Takes the skin with this background; another colour gives the first dark or the first light skin. */
    void palette(int background) {
        int[] skin = SKINS[light(background) ? 4 : 0];
        for (int[] known : SKINS) if (known[0] == (background & 0xffffff)) skin = known;
        BG = 0xff000000 | skin[0]; PANEL = 0xff000000 | skin[1]; RAISED = 0xff000000 | skin[2]; LINE = 0xff000000 | skin[3];
        INK = 0xff000000 | skin[4]; MUTED = 0xff000000 | skin[5]; ACCENT = 0xff000000 | skin[6]; ON_ACCENT = 0xff000000 | skin[7];
        GOOD = 0xff000000 | skin[8]; BUSY = 0xff000000 | skin[9]; BAD = 0xff000000 | skin[10];
        SUB = 0xff000000 | skin[11]; CARD_BORDER = 0xff000000 | skin[12]; DIVIDER = 0xff000000 | skin[13];
        ACCENT_SOFT = 0xff000000 | skin[14]; ACCENT_INK = 0xff000000 | skin[15];
        BUSY_SOFT = 0xff000000 | skin[16]; BUSY_INK = 0xff000000 | skin[17]; BUSY_DOT = 0xff000000 | skin[18];
        GOOD_SOFT = 0xff000000 | skin[19]; GOOD_INK = 0xff000000 | skin[20]; GOOD_DOT = 0xff000000 | skin[21];
        BAD_SOFT = 0xff000000 | skin[22];
    }

    // ---- Semantic design tokens derived directly from the active skin (no hardcoded light theme bleed)
    int solidInk() { return ACCENT; }
    int onSolidInk() { return ON_ACCENT; }
    int heroBg() { return PANEL; }
    int heroTintBg() { return mix(ACCENT, PANEL, light(BG) ? .06f : .07f); }
    int heroBorder() { return mix(ACCENT, CARD_BORDER, light(BG) ? .20f : .24f); }
    int heroFg() { return INK; }
    int heroMuted() { return MUTED; }
    int heroChipBg() { return RAISED; }
    int cardBorder() { return CARD_BORDER; }
    int dividerColor() { return DIVIDER; }
    int subSurface() { return SUB; }
    int accentSoft() { return ACCENT_SOFT; }
    int accentInk() { return ACCENT_INK; }
    int busySoft() { return BUSY_SOFT; }
    int busyInk() { return BUSY_INK; }
    int busyDot() { return BUSY_DOT; }
    int goodSoft() { return GOOD_SOFT; }
    int goodInk() { return GOOD_INK; }
    int goodDot() { return GOOD_DOT; }
    int badSoft() { return BAD_SOFT; }

    static String toolMono(String tool) {
        if ("agy".equals(tool)) return "AG";
        if ("codex".equals(tool)) return "CX";
        if ("claude".equals(tool)) return "CC";
        if ("opencode".equals(tool)) return "OC";
        if ("qoder".equals(tool)) return "QD";
        if ("cline".equals(tool)) return "CL";
        if ("pi".equals(tool)) return "PI";
        return "";
    }
    int toolBadgeBg(String tool) {
        boolean day = light(BG);
        if ("agy".equals(tool)) return day ? 0xffece8fd : 0xff261d4a;
        if ("codex".equals(tool)) return day ? 0xffe6eefd : 0xff17284a;
        if ("claude".equals(tool)) return day ? 0xfffbe9e1 : 0xff3a2318;
        if ("opencode".equals(tool)) return day ? 0xffe6f6ec : 0xff153224;
        if ("qoder".equals(tool)) return day ? 0xffeef1ff : 0xff1b2548;
        if ("cline".equals(tool)) return day ? 0xfff3e8ff : 0xff2d1b4e;
        if ("pi".equals(tool)) return day ? 0xfffef3c7 : 0xff3b2a10;
        return RAISED;
    }
    int toolBadgeFg(String tool) {
        boolean day = light(BG);
        if ("agy".equals(tool)) return day ? 0xff5536c9 : 0xffbca9f8;
        if ("codex".equals(tool)) return day ? 0xff1d4fbf : 0xff9dbbf7;
        if ("claude".equals(tool)) return day ? 0xffa9441d : 0xfff7b593;
        if ("opencode".equals(tool)) return day ? 0xff15803d : 0xff86efac;
        if ("qoder".equals(tool)) return day ? 0xff2445c8 : 0xff9dbbf7;
        if ("cline".equals(tool)) return day ? 0xff7c3aed : 0xffd8b4fe;
        if ("pi".equals(tool)) return day ? 0xff9a5b00 : 0xfffcd34d;
        return INK;
    }

    int dp(float value) { return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value, activity.getResources().getDisplayMetrics())); }
    /** Crisp 0.5dp hairline divider/stroke on high-DPI displays. */
    int hairline() { return Math.max(1, Math.round(activity.getResources().getDisplayMetrics().density * 0.5f)); }
    /** Subtle surface rim derived from the active skin's ink, avoiding cold shadows on warm skins. */
    int rim() { return light(BG) ? tint(INK, 18) : tint(0xffffffff, 20); }
    /** Soft elevation for cards on light themes; flat layered contrast on dark themes. */
    void elevate(View view, float depthDp) {
        if (!light(BG)) { view.setElevation(0); return; }
        view.setElevation(dp(depthDp));
        if (Build.VERSION.SDK_INT >= 28) {
            view.setOutlineAmbientShadowColor(tint(INK, 16));
            view.setOutlineSpotShadowColor(tint(INK, 26));
        }
    }
    void haptic(View view, int kind) { if (view != null) view.performHapticFeedback(kind); }
    static int tint(int color, int alpha) { return Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color)); }
    /** One colour laid over another at the given share, as the pages do with color-mix. */
    static int mix(int over, int under, float share) {
        return Color.rgb(Math.round(Color.red(over) * share + Color.red(under) * (1 - share)), Math.round(Color.green(over) * share + Color.green(under) * (1 - share)),
            Math.round(Color.blue(over) * share + Color.blue(under) * (1 - share)));
    }
    private static Typeface weight(int w) {
        return Build.VERSION.SDK_INT >= 28 ? Typeface.create(Typeface.DEFAULT, w, false) : (w >= 600 ? Typeface.DEFAULT_BOLD : Typeface.DEFAULT);
    }

    TextView text(String value, float size, int color) {
        TextView view = new TextView(activity);
        view.setText(value); view.setTextSize(size); view.setTextColor(color); view.setLineSpacing(dp(3), 1f);
        return view;
    }
    TextView medium(String value, float size, int color) { TextView view = text(value, size, color); view.setTypeface(weight(500)); return view; }
    TextView semibold(String value, float size, int color) { TextView view = text(value, size, color); view.setTypeface(weight(600)); return view; }
    TextView bold(String value, float size, int color) { TextView view = text(value, size, color); view.setTypeface(weight(700)); return view; }
    TextView mono(String value, float size, int color) {
        TextView view = text(value, size, color);
        view.setTypeface(Typeface.MONOSPACE);
        view.setFontFeatureSettings("tnum");
        return view;
    }
    /** Tabular sans-serif KPI number, matching modern iOS/macOS metric displays. */
    TextView figure(String value, float size, int color) {
        TextView view = semibold(value, size, color);
        view.setFontFeatureSettings("tnum");
        view.setLetterSpacing(-0.02f);
        view.setSingleLine(true);
        return view;
    }
    /** One line that ends in an ellipsis when it does not fit. */
    TextView line(String value, float size, int color, boolean strong) {
        TextView view = strong ? semibold(value, size, color) : text(value, size, color);
        view.setSingleLine(true); view.setEllipsize(TextUtils.TruncateAt.END);
        return view;
    }
    /** A title may use the available width before it is shortened. */
    TextView title(String value, float size, int color, boolean strong) {
        TextView view = strong ? semibold(value, size, color) : medium(value, size, color);
        view.setMaxLines(2); view.setEllipsize(TextUtils.TruncateAt.END);
        return view;
    }
    GradientDrawable shape(int fill, int stroke, float radius) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fill); drawable.setCornerRadius(dp(radius)); if (stroke != 0) drawable.setStroke(dp(1), stroke);
        return drawable;
    }
    GradientDrawable hairlineShape(int fill, int stroke, float radius) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fill); drawable.setCornerRadius(dp(radius)); if (stroke != 0) drawable.setStroke(hairline(), stroke);
        return drawable;
    }
    LinearLayout cardBox(float radius) {
        LinearLayout card = column();
        card.setBackground(shape(PANEL, cardBorder(), radius));
        card.setClipToOutline(true);
        elevate(card, 1.5f);
        return card;
    }
    View insetDivider(int leftDp) {
        View line = new View(activity);
        line.setBackgroundColor(dividerColor());
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, dp(1));
        if (leftDp > 0) params.leftMargin = dp(leftDp);
        line.setLayoutParams(params);
        return line;
    }
    EditText field(String hint, int type) {
        EditText box = new EditText(activity);
        box.setHint(hint); box.setHintTextColor(MUTED); box.setTextColor(INK); box.setTextSize(15.5f); box.setSingleLine(true);
        box.setInputType(type); box.setBackground(shape(PANEL, cardBorder(), 16)); box.setPadding(dp(16), 0, dp(16), 0); box.setMinHeight(dp(50));
        return box;
    }
    /** A button: 0 the main action, 1 an ordinary one, 2 only text, 3 soft danger. */
    TextView button(String label, int kind, Runnable action) {
        int ink = kind == 0 ? onSolidInk() : kind == 3 ? BAD : kind == 1 ? INK : MUTED;
        TextView view = semibold(label, 15, ink);
        view.setGravity(Gravity.CENTER); view.setMinHeight(dp(kind == 0 ? 50 : 48)); view.setPadding(dp(16), dp(6), dp(16), dp(6));
        if (kind == 0) view.setBackground(shape(solidInk(), 0, 14));
        else if (kind == 1) view.setBackground(shape(PANEL, cardBorder(), 14));
        else if (kind == 3) view.setBackground(shape(badSoft(), 0, 14));
        press(view, action);
        Motion.springPress(view, 0.97f);
        return view;
    }
    void press(View view, Runnable action) {
        view.setClickable(true); view.setFocusable(true);
        Drawable base = view.getBackground();
        if (!(base instanceof RippleDrawable)) {
            int ripple = tint(INK, light(BG) ? 20 : 34);
            Drawable mask = base != null ? null : shape(0xffffffff, 0, 14);
            view.setBackground(new RippleDrawable(ColorStateList.valueOf(ripple), base, mask));
        }
        view.setOnClickListener(v -> action.run());
    }
    LinearLayout.LayoutParams below(int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.topMargin = dp(top);
        return params;
    }
    LinearLayout column() { LinearLayout column = new LinearLayout(activity); column.setOrientation(LinearLayout.VERTICAL); return column; }
    LinearLayout row() { LinearLayout row = new LinearLayout(activity); row.setGravity(Gravity.CENTER_VERTICAL); return row; }
    /** A clean navigation bar for secondary screens: back circle button, title, optional right actions. */
    LinearLayout navBar(String title, Runnable onBack, View... actions) {
        LinearLayout bar = row();
        bar.setMinimumHeight(dp(48));
        View back = circleAction(R.drawable.ic_back, "返回", false, onBack);
        LinearLayout.LayoutParams backParams = (LinearLayout.LayoutParams) back.getLayoutParams();
        backParams.leftMargin = -dp(4);
        backParams.rightMargin = dp(6);
        bar.addView(back, backParams);
        TextView named = bold(title, 20, INK);
        named.setSingleLine(true); named.setEllipsize(TextUtils.TruncateAt.END);
        named.setLetterSpacing(-0.015f);
        bar.addView(named, new LinearLayout.LayoutParams(0, -2, 1));
        for (View action : actions) if (action != null) bar.addView(action);
        return bar;
    }
    LinearLayout settingGroup(LinearLayout page, String label) {
        if (label != null && !label.isEmpty()) {
            TextView heading = semibold(label, 14, INK);
            heading.setPadding(dp(4), 0, dp(4), 0);
            page.addView(heading, below(20));
        }
        LinearLayout group = cardBox(22);
        page.addView(group, below(label != null && !label.isEmpty() ? 10 : 16));
        return group;
    }
    void setting(LinearLayout group, String label, String description, String value, Runnable action) {
        setting(group, 0, 0, label, description, value, MUTED, action);
    }
    void setting(LinearLayout group, int iconRes, int iconTint, String label, String description, String value, Runnable action) {
        setting(group, iconRes, iconTint, label, description, value, MUTED, action);
    }
    void setting(LinearLayout group, int iconRes, int iconTint, String label, String description, String value, int valueColor, Runnable action) {
        LinearLayout row = settingBody(group, iconRes, iconTint, label, description);
        if (value != null && !value.isEmpty()) {
            TextView status = valueColor != MUTED ? semibold(value, 12.5f, valueColor) : line(value, 13, valueColor, false);
            status.setSingleLine(true); status.setEllipsize(TextUtils.TruncateAt.END); status.setMaxWidth(dp(120));
            if (valueColor != MUTED) {
                status.setPadding(dp(8), dp(2), dp(8), dp(2));
                status.setBackground(shape(tint(valueColor, 30), 0, 99));
            }
            LinearLayout.LayoutParams gap = new LinearLayout.LayoutParams(-2, -2); gap.setMargins(dp(10), 0, dp(6), 0); row.addView(status, gap);
        }
        row.addView(icon(R.drawable.ic_chevron, tint(MUTED, 150), 16));
        press(row, action);
    }
    void settingSwitch(LinearLayout group, String label, String description, boolean on, Runnable action) {
        settingSwitch(group, 0, 0, label, description, on, action);
    }
    void settingSwitch(LinearLayout group, int iconRes, int iconTint, String label, String description, boolean on, Runnable action) {
        LinearLayout row = settingBody(group, iconRes, iconTint, label, description);
        View toggle = toggleView(on, label);
        LinearLayout.LayoutParams gap = new LinearLayout.LayoutParams(dp(50), dp(30)); gap.leftMargin = dp(12);
        row.addView(toggle, gap);
        press(row, () -> { haptic(row, HapticFeedbackConstants.CLOCK_TICK); action.run(); });
    }
    /** A crisp pill toggle switch matching web/ui/sheet.css (.rs-switch). */
    View toggleView(boolean on, String label) {
        FrameLayout track = new FrameLayout(activity);
        track.setBackground(shape(on ? goodDot() : mix(MUTED, RAISED, .32f), 0, 15));
        View thumb = new View(activity);
        thumb.setBackground(shape(0xffffffff, 0, 13));
        thumb.setElevation(dp(2));
        FrameLayout.LayoutParams knob = new FrameLayout.LayoutParams(dp(26), dp(26), Gravity.CENTER_VERTICAL | (on ? Gravity.END : Gravity.START));
        knob.setMargins(dp(2), 0, dp(2), 0);
        track.addView(thumb, knob);
        track.setContentDescription(label + (on ? "，已开启" : "，已关闭"));
        return track;
    }
    private LinearLayout settingBody(LinearLayout group, int iconRes, int iconTint, String label, String description) {
        if (group.getChildCount() > 0) {
            group.addView(insetDivider(iconRes != 0 ? 64 : 16));
        }
        LinearLayout row = row();
        row.setPadding(dp(14), dp(12), dp(14), dp(12));
        row.setMinimumHeight(dp(56));
        if (iconRes != 0) {
            View badge = tile(iconRes, iconTint == 0 ? ACCENT : iconTint, 36);
            LinearLayout.LayoutParams badgeParams = (LinearLayout.LayoutParams) badge.getLayoutParams();
            badgeParams.rightMargin = dp(12);
            row.addView(badge, badgeParams);
        }
        LinearLayout copy = column();
        TextView title = medium(label, 15, INK);
        title.setSingleLine(true); title.setEllipsize(TextUtils.TruncateAt.END);
        copy.addView(title);
        if (description != null && !description.isEmpty()) copy.addView(text(description, 12.5f, MUTED), below(2));
        row.addView(copy, new LinearLayout.LayoutParams(0, -2, 1));
        group.addView(row);
        return row;
    }
    /** A screen's content: one column with the page margins. */
    LinearLayout page(int top) { LinearLayout page = column(); page.setPadding(dp(16), dp(top), dp(16), dp(24)); return page; }

    ImageView icon(int drawable, int color, int size) {
        ImageView view = new ImageView(activity);
        view.setImageResource(drawable); view.setImageTintList(ColorStateList.valueOf(color));
        view.setLayoutParams(new LinearLayout.LayoutParams(dp(size), dp(size)));
        return view;
    }
    /** An icon on a rounded tile tinted with its own colour: a folder or a setting glyph. */
    View tile(int drawable, int color, int size) {
        FrameLayout tile = new FrameLayout(activity);
        tile.setBackground(shape(mix(color, BG, .15f), 0, size * .30f));
        ImageView image = icon(drawable, color, Math.round(size * .52f));
        tile.addView(image, new FrameLayout.LayoutParams(dp(size * .52f), dp(size * .52f), Gravity.CENTER));
        tile.setLayoutParams(new LinearLayout.LayoutParams(dp(size), dp(size)));
        return tile;
    }
    /** An icon tile with explicit fill and stroke colors (e.g. calm neutral quick-action tiles or status folders). */
    View tintedTile(int drawable, int fill, int iconColor, int size, float radius) {
        FrameLayout tile = new FrameLayout(activity);
        tile.setBackground(shape(fill, 0, radius));
        int glyph = Math.round(size * .50f);
        tile.addView(icon(drawable, iconColor, glyph), new FrameLayout.LayoutParams(dp(glyph), dp(glyph), Gravity.CENTER));
        tile.setLayoutParams(new LinearLayout.LayoutParams(dp(size), dp(size)));
        return tile;
    }
    /** Tool identity squircle badge: two-letter monogram (AG / CX / CC) or terminal icon for shell. */
    View toolBadge(String tool, int sizeDp) {
        String mono = toolMono(tool);
        int bg = toolBadgeBg(tool), fg = toolBadgeFg(tool);
        float radius = sizeDp >= 40 ? 13f : sizeDp >= 34 ? 11f : 9f;
        FrameLayout badge = new FrameLayout(activity);
        badge.setBackground(shape(bg, 0, radius));
        if (!mono.isEmpty()) {
            float sp = sizeDp >= 42 ? 13f : sizeDp >= 38 ? 12f : 10.5f;
            TextView label = bold(mono, sp, fg);
            label.setIncludeFontPadding(false);
            label.setLetterSpacing(0.02f);
            label.setGravity(Gravity.CENTER);
            badge.addView(label, new FrameLayout.LayoutParams(-1, -1, Gravity.CENTER));
        } else {
            int glyph = Math.round(sizeDp * .48f);
            badge.addView(icon(R.drawable.ic_shell, fg, glyph), new FrameLayout.LayoutParams(dp(glyph), dp(glyph), Gravity.CENTER));
        }
        badge.setLayoutParams(new LinearLayout.LayoutParams(dp(sizeDp), dp(sizeDp)));
        return badge;
    }
    /** Circular 42dp visual action button inside a 48dp touch target, solid dark or outlined white. */
    View circleAction(int drawable, String says, boolean solid, Runnable action) {
        FrameLayout box = new FrameLayout(activity), disc = new FrameLayout(activity);
        disc.setBackground(shape(solid ? solidInk() : PANEL, solid ? 0 : cardBorder(), 21));
        if (solid) elevate(disc, 4f);
        disc.addView(icon(drawable, solid ? onSolidInk() : INK, 20), new FrameLayout.LayoutParams(dp(20), dp(20), Gravity.CENTER));
        box.addView(disc, new FrameLayout.LayoutParams(dp(42), dp(42), Gravity.CENTER));
        box.setContentDescription(says);
        box.setLayoutParams(new LinearLayout.LayoutParams(dp(48), dp(48)));
        press(box, action);
        Motion.springPress(box, 0.92f);
        return box;
    }
    /**
     * The top of one of the app's own lists: the name of its tab large, a line under it that says how things stand,
     * and at the right what is done there.
     */
    LinearLayout head(String title, View under, View... actions) {
        LinearLayout head = row();
        LinearLayout titles = column();
        TextView name = bold(title, 31, INK);
        name.setIncludeFontPadding(false); name.setLineSpacing(0, 1f); name.setLetterSpacing(-0.02f);
        titles.addView(name);
        if (under != null) titles.addView(under, below(4));
        head.addView(titles, new LinearLayout.LayoutParams(0, -2, 1));
        for (View action : actions) if (action != null) head.addView(action);
        return head;
    }
    /** The line under a list's name: a dot in the colour of how things stand, then the words. */
    TextView standing(String text) {
        TextView line = line(text, 13, MUTED, false);
        line.setCompoundDrawablePadding(dp(7));
        return line;
    }
    void stands(TextView line, String text, int color, int dot) {
        line.setText(text); line.setTextColor(color);
        GradientDrawable mark = shape(dot, 0, 4);
        mark.setBounds(0, 0, dp(8), dp(8));
        line.setCompoundDrawables(mark, null, null, null);
    }
    /** An icon that is a button of its own, large enough for a thumb. */
    View iconButton(int drawable, String says, Runnable action) { return iconButton(drawable, says, MUTED, action); }
    View iconButton(int drawable, String says, int color, Runnable action) {
        FrameLayout box = new FrameLayout(activity);
        box.addView(icon(drawable, color, 22), new FrameLayout.LayoutParams(dp(22), dp(22), Gravity.CENTER));
        box.setContentDescription(says);
        box.setLayoutParams(new LinearLayout.LayoutParams(dp(48), dp(48)));
        press(box, action);
        Motion.springPress(box, 0.92f);
        return box;
    }
    /** A small action on a card: its icon on a quiet rounded tile, with room around it for a thumb. */
    View discButton(int drawable, String says, Runnable action) { return discButton(drawable, says, RAISED, MUTED, action); }
    View discButton(int drawable, String says, int fill, int iconColor, Runnable action) {
        FrameLayout box = new FrameLayout(activity), disc = new FrameLayout(activity);
        disc.setBackground(shape(fill, 0, 12));
        disc.addView(icon(drawable, iconColor, 17), new FrameLayout.LayoutParams(dp(17), dp(17), Gravity.CENTER));
        box.addView(disc, new FrameLayout.LayoutParams(dp(38), dp(38), Gravity.CENTER));
        box.setContentDescription(says);
        box.setLayoutParams(new LinearLayout.LayoutParams(dp(48), dp(48)));
        press(box, action);
        Motion.springPress(box, 0.92f);
        return box;
    }
    View dot(int color, int size) {
        View dot = new View(activity);
        dot.setBackground(shape(color, 0, size));
        dot.setLayoutParams(new LinearLayout.LayoutParams(dp(size), dp(size)));
        return dot;
    }
    /** A glowing status dot inside a soft halo ring (online green / live amber). */
    View haloDot(int coreColor, int haloColor) {
        FrameLayout wrap = new FrameLayout(activity);
        wrap.setBackground(shape(haloColor, 0, 7));
        View core = new View(activity);
        core.setBackground(shape(coreColor, 0, 4));
        wrap.addView(core, new FrameLayout.LayoutParams(dp(8), dp(8), Gravity.CENTER));
        wrap.setLayoutParams(new LinearLayout.LayoutParams(dp(14), dp(14)));
        return wrap;
    }
    /** Status pill with explicit text, fill, and optional dot colour (0 omits dot). */
    TextView statusPill(String label, int fg, int bg, int dotColor) {
        TextView pill = semibold(label, 12, fg);
        pill.setSingleLine(true);
        pill.setGravity(Gravity.CENTER_VERTICAL);
        pill.setMinHeight(dp(26));
        pill.setPadding(dp(9), dp(2), dp(9), dp(2));
        pill.setBackground(shape(bg, 0, 99));
        if (dotColor != 0) {
            GradientDrawable mark = shape(dotColor, 0, 3);
            mark.setBounds(0, 0, dp(6), dp(6));
            pill.setCompoundDrawables(mark, null, null, null);
            pill.setCompoundDrawablePadding(dp(5));
        }
        return pill;
    }
    /** What a task is doing: its colour as a dot and as the text, on a wash of the same colour. */
    TextView pill(String label, int color) {
        return statusPill(label, color, tint(color, 34), color);
    }

    /** Choices on a sheet that rises from the bottom, within reach of a thumb. notes may hold null; danger marks one choice, or is -1. */
    private LinearLayout sheetBox(String title, String note, int noteLines) {
        LinearLayout box = column();
        box.setPadding(dp(16), dp(8), dp(16), dp(16));
        GradientDrawable back = new GradientDrawable();
        back.setColor(BG); back.setCornerRadii(new float[]{dp(24), dp(24), dp(24), dp(24), 0, 0, 0, 0});
        box.setBackground(back);
        View grip = new View(activity);
        grip.setBackground(shape(tint(MUTED, 97), 0, 3));
        LinearLayout.LayoutParams middle = new LinearLayout.LayoutParams(dp(38), dp(5));
        middle.gravity = Gravity.CENTER_HORIZONTAL; middle.bottomMargin = dp(12);
        box.addView(grip, middle);
        TextView named = bold(title, 17, INK);
        named.setGravity(Gravity.CENTER); named.setSingleLine(true); named.setEllipsize(TextUtils.TruncateAt.END);
        box.addView(named);
        if (note != null && !note.isEmpty()) {
            TextView under = text(note, 13.5f, MUTED);
            under.setGravity(Gravity.CENTER);
            if (noteLines > 0) { under.setMaxLines(noteLines); under.setEllipsize(TextUtils.TruncateAt.END); }
            box.addView(under, below(4));
        }
        return box;
    }
    private void raise(Dialog dialog, LinearLayout box, boolean typing) {
        View content = box;
        if (typing) {
            android.widget.ScrollView scroller = new android.widget.ScrollView(activity);
            scroller.setFillViewport(false);
            scroller.setOverScrollMode(View.OVER_SCROLL_IF_CONTENT_SCROLLS);
            scroller.addView(box, new android.view.ViewGroup.LayoutParams(-1, -2));
            content = scroller;
        }
        dialog.setContentView(content);
        Window window = dialog.getWindow();
        if (window != null && typing) window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_STATE_VISIBLE | android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        dialog.show();
        if (window == null) return;
        window.setBackgroundDrawable(new ColorDrawable(Color.TRANSPARENT));
        window.setDimAmount(0.36f);
        window.setLayout(-1, -2); window.setGravity(Gravity.BOTTOM);
        content.setAlpha(0f);
        content.setTranslationY(dp(36));
        content.animate().alpha(1f).translationY(0f).setDuration(240).setInterpolator(new DecelerateInterpolator(2.2f)).start();
    }
    /** A sheet with a box to write in and one button, which hands over what was written. */
    void write(String title, String note, String hint, String yes, java.util.function.Consumer<String> written) {
        final Dialog dialog = new Dialog(activity);
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        LinearLayout box = sheetBox(title, note, 3);
        final EditText field = field(hint, android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        field.setSingleLine(false); field.setMinLines(2); field.setMaxLines(6); field.setGravity(Gravity.TOP | Gravity.START);
        field.setBackground(shape(PANEL, cardBorder(), 16)); field.setPadding(dp(16), dp(13), dp(16), dp(13));
        box.addView(field, below(14));
        box.addView(button(yes, 0, () -> {
            String text = field.getText().toString().trim();
            if (text.isEmpty()) return;
            dialog.dismiss(); written.accept(text);
        }), below(10));
        box.addView(button("取消", 2, dialog::dismiss), below(4));
        raise(dialog, box, true);
        field.requestFocus();
    }
    void sheet(String title, String note, String[] labels, String[] notes, int danger, IntConsumer picked) {
        final Dialog dialog = new Dialog(activity);
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        LinearLayout box = sheetBox(title, note, 0);
        LinearLayout group = cardBox(20), apart = cardBox(20);
        for (int i = 0; i < labels.length; i++) {
            String said = notes != null && notes[i] != null ? notes[i] : "";
            boolean current = said.startsWith("当前使用");
            if (current) said = said.replaceFirst("^当前使用( · )?", "");
            LinearLayout target = i == danger ? apart : group;
            if (target.getChildCount() > 0) target.addView(insetDivider(16));
            LinearLayout item = row();
            item.setMinimumHeight(dp(54)); item.setPadding(dp(16), dp(10), dp(14), dp(10));
            LinearLayout words = column();
            TextView name = medium(labels[i], 15.5f, i == danger ? BAD : current ? accentInk() : INK);
            if (current || i == danger) name.setTypeface(weight(600));
            if (i == danger) { name.setGravity(Gravity.CENTER); words.setGravity(Gravity.CENTER_HORIZONTAL); }
            words.addView(name);
            if (!said.isEmpty()) words.addView(text(said, 12.5f, MUTED), below(2));
            item.addView(words, new LinearLayout.LayoutParams(0, -2, 1));
            if (current) item.addView(icon(R.drawable.ic_check, accentInk(), 20));
            item.setContentDescription(labels[i] + (current ? "，当前使用" : ""));
            final int at = i;
            press(item, () -> { dialog.dismiss(); picked.accept(at); });
            target.addView(item);
        }
        if (group.getChildCount() > 0) box.addView(group, below(14));
        if (apart.getChildCount() > 0) box.addView(apart, below(group.getChildCount() > 0 ? 10 : 14));
        box.addView(button("取消", 1, dialog::dismiss), below(10));
        raise(dialog, box, false);
    }
    /** A bottom confirmation sheet matching web/ui/sheet.js, replacing raw system AlertDialogs. */
    void confirmSheet(String title, String note, String yesLabel, boolean danger, Runnable onYes) {
        final Dialog dialog = new Dialog(activity);
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        LinearLayout box = sheetBox(title, null, 0);
        if (note != null && !note.isEmpty()) {
            LinearLayout card = column();
            card.setPadding(dp(14), dp(12), dp(14), dp(12));
            card.setBackground(shape(danger ? badSoft() : accentSoft(), 0, 14));
            card.addView(text(note, 13.5f, INK));
            box.addView(card, below(12));
        }
        box.addView(button(yesLabel, danger ? 3 : 0, () -> { dialog.dismiss(); onYes.run(); }), below(14));
        box.addView(button("取消", 1, dialog::dismiss), below(8));
        raise(dialog, box, false);
    }
    /** A single-line input sheet (e.g. renaming a computer). */
    void promptSheet(String title, String initial, String hint, String yesLabel, java.util.function.Consumer<String> onSave) {
        final Dialog dialog = new Dialog(activity);
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        LinearLayout box = sheetBox(title, null, 0);
        final EditText field = field(hint, android.text.InputType.TYPE_CLASS_TEXT);
        field.setText(initial);
        field.setSelection(initial == null ? 0 : initial.length());
        field.setBackground(shape(PANEL, cardBorder(), 16));
        box.addView(field, below(14));
        box.addView(button(yesLabel, 0, () -> {
            String value = field.getText().toString().trim();
            if (value.isEmpty()) return;
            dialog.dismiss();
            onSave.accept(value);
        }), below(12));
        box.addView(button("取消", 1, dialog::dismiss), below(8));
        raise(dialog, box, true);
        field.requestFocus();
    }
}
