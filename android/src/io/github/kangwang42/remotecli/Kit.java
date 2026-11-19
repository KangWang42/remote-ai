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
import android.text.TextUtils;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.Window;
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
    static final int PAPER = 0xfffbfbfc;
    private final Activity activity;
    int BG, PANEL, RAISED, LINE, INK, MUTED, ACCENT, ON_ACCENT, GOOD, BUSY, BAD;

    Kit(Activity activity) { this.activity = activity; }

    static boolean light(int shade) { return (Color.red(shade) * 299 + Color.green(shade) * 587 + Color.blue(shade) * 114) / 1000 > 150; }
    /** Takes the skin with this background; another colour gives the first dark or the first light skin. */
    void palette(int background) {
        int[] skin = SKINS[light(background) ? 4 : 0];
        for (int[] known : SKINS) if (known[0] == (background & 0xffffff)) skin = known;
        BG = 0xff000000 | skin[0]; PANEL = 0xff000000 | skin[1]; RAISED = 0xff000000 | skin[2]; LINE = 0xff000000 | skin[3];
        INK = 0xff000000 | skin[4]; MUTED = 0xff000000 | skin[5]; ACCENT = 0xff000000 | skin[6]; ON_ACCENT = 0xff000000 | skin[7];
        GOOD = 0xff000000 | skin[8]; BUSY = 0xff000000 | skin[9]; BAD = 0xff000000 | skin[10];
    }

    int dp(float value) { return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value, activity.getResources().getDisplayMetrics())); }
    static int tint(int color, int alpha) { return Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color)); }
    /** One colour laid over another at the given share, as the pages do with color-mix. */
    static int mix(int over, int under, float share) {
        return Color.rgb(Math.round(Color.red(over) * share + Color.red(under) * (1 - share)), Math.round(Color.green(over) * share + Color.green(under) * (1 - share)),
            Math.round(Color.blue(over) * share + Color.blue(under) * (1 - share)));
    }

    TextView text(String value, float size, int color) {
        TextView view = new TextView(activity);
        view.setText(value); view.setTextSize(size); view.setTextColor(color); view.setLineSpacing(dp(3), 1f);
        return view;
    }
    TextView bold(String value, float size, int color) { TextView view = text(value, size, color); view.setTypeface(Typeface.DEFAULT_BOLD); return view; }
    /** One line that ends in an ellipsis when it does not fit. */
    TextView line(String value, float size, int color, boolean strong) {
        TextView view = strong ? bold(value, size, color) : text(value, size, color);
        view.setSingleLine(true); view.setEllipsize(TextUtils.TruncateAt.END);
        return view;
    }
    /** A title may use the available width before it is shortened. */
    TextView title(String value, float size, int color, boolean strong) {
        TextView view = strong ? bold(value, size, color) : text(value, size, color);
        view.setMaxLines(2); view.setEllipsize(TextUtils.TruncateAt.END);
        return view;
    }
    GradientDrawable shape(int fill, int stroke, float radius) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fill); drawable.setCornerRadius(dp(radius)); if (stroke != 0) drawable.setStroke(dp(1), stroke);
        return drawable;
    }
    EditText field(String hint, int type) {
        EditText box = new EditText(activity);
        box.setHint(hint); box.setHintTextColor(MUTED); box.setTextColor(INK); box.setTextSize(16); box.setSingleLine(true);
        box.setInputType(type); box.setBackground(shape(PANEL, LINE, 14)); box.setPadding(dp(16), 0, dp(16), 0); box.setMinHeight(dp(52));
        return box;
    }
    /** A button: 0 the main action, 1 an ordinary one, 2 only text, 3 soft danger. */
    TextView button(String label, int kind, Runnable action) {
        int ink = kind == 0 ? ON_ACCENT : kind == 3 ? BAD : kind == 1 ? INK : MUTED;
        TextView view = bold(label, 15.5f, ink);
        view.setGravity(Gravity.CENTER); view.setMinHeight(dp(kind == 0 ? 50 : 48)); view.setPadding(dp(16), dp(6), dp(16), dp(6));
        if (kind == 0) view.setBackground(shape(ACCENT, 0, 14));
        else if (kind == 1) view.setBackground(shape(PANEL, mix(LINE, PANEL, .65f), 14));
        else if (kind == 3) view.setBackground(shape(mix(BAD, BG, .12f), 0, 14));
        press(view, action);
        return view;
    }
    void press(View view, Runnable action) {
        view.setClickable(true); view.setFocusable(true);
        Drawable base = view.getBackground();
        if (!(base instanceof RippleDrawable)) {
            int ripple = tint(INK, light(BG) ? 26 : 36);
            Drawable mask = base != null ? null : shape(0xffffffff, 0, 12);
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
    /** A clean navigation bar for secondary screens: back icon button, title, optional right actions. */
    LinearLayout navBar(String title, Runnable onBack, View... actions) {
        LinearLayout bar = row();
        bar.setMinimumHeight(dp(48));
        View back = iconButton(R.drawable.ic_back, "返回", INK, onBack);
        LinearLayout.LayoutParams backParams = (LinearLayout.LayoutParams) back.getLayoutParams();
        backParams.leftMargin = -dp(12);
        bar.addView(back, backParams);
        TextView named = bold(title, 19, INK);
        named.setSingleLine(true); named.setEllipsize(TextUtils.TruncateAt.END);
        bar.addView(named, new LinearLayout.LayoutParams(0, -2, 1));
        for (View action : actions) if (action != null) bar.addView(action);
        return bar;
    }
    LinearLayout settingGroup(LinearLayout page, String label) {
        if (label != null && !label.isEmpty()) {
            TextView heading = bold(label, 12.5f, MUTED);
            heading.setPadding(dp(4), 0, dp(4), 0);
            page.addView(heading, below(20));
        }
        LinearLayout group = column();
        group.setBackground(shape(PANEL, mix(LINE, PANEL, .6f), 16));
        group.setClipToOutline(true);
        page.addView(group, below(label != null && !label.isEmpty() ? 8 : 16));
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
            TextView status = valueColor != MUTED ? bold(value, 13, valueColor) : line(value, 13, valueColor, false);
            status.setSingleLine(true); status.setEllipsize(TextUtils.TruncateAt.END); status.setMaxWidth(dp(120));
            LinearLayout.LayoutParams gap = new LinearLayout.LayoutParams(-2, -2); gap.setMargins(dp(10), 0, dp(6), 0); row.addView(status, gap);
        }
        row.addView(icon(R.drawable.ic_chevron, tint(MUTED, 150), 15));
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
        press(row, action);
    }
    /** A crisp pill toggle switch matching web/ui/sheet.css (.rs-switch). */
    View toggleView(boolean on, String label) {
        FrameLayout track = new FrameLayout(activity);
        track.setBackground(shape(on ? GOOD : mix(MUTED, RAISED, .32f), 0, 15));
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
            View line = new View(activity);
            line.setBackgroundColor(mix(LINE, PANEL, .75f));
            LinearLayout.LayoutParams divider = new LinearLayout.LayoutParams(-1, dp(1));
            divider.setMargins(dp(iconRes != 0 ? 58 : 16), 0, 0, 0);
            group.addView(line, divider);
        }
        LinearLayout row = row();
        row.setPadding(dp(14), dp(11), dp(14), dp(11));
        row.setMinimumHeight(dp(54));
        if (iconRes != 0) {
            View badge = tile(iconRes, iconTint == 0 ? ACCENT : iconTint, 32);
            LinearLayout.LayoutParams badgeParams = (LinearLayout.LayoutParams) badge.getLayoutParams();
            badgeParams.rightMargin = dp(12);
            row.addView(badge, badgeParams);
        }
        LinearLayout copy = column();
        copy.addView(line(label, 15.5f, INK, false));
        if (description != null && !description.isEmpty()) copy.addView(text(description, 12.5f, MUTED), below(2));
        row.addView(copy, new LinearLayout.LayoutParams(0, -2, 1));
        group.addView(row);
        return row;
    }
    /** A screen's content: one column with the page margins. */
    LinearLayout page(int top) { LinearLayout page = column(); page.setPadding(dp(18), dp(top), dp(18), dp(28)); return page; }

    ImageView icon(int drawable, int color, int size) {
        ImageView view = new ImageView(activity);
        view.setImageResource(drawable); view.setImageTintList(ColorStateList.valueOf(color));
        view.setLayoutParams(new LinearLayout.LayoutParams(dp(size), dp(size)));
        return view;
    }
    /** An icon on a rounded tile tinted with its own colour: the tool a task runs, or a folder. */
    View tile(int drawable, int color, int size) {
        FrameLayout tile = new FrameLayout(activity);
        tile.setBackground(shape(mix(color, BG, .16f), 0, size * .3f));
        ImageView image = icon(drawable, color, Math.round(size * .55f));
        tile.addView(image, new FrameLayout.LayoutParams(dp(size * .55f), dp(size * .55f), Gravity.CENTER));
        tile.setLayoutParams(new LinearLayout.LayoutParams(dp(size), dp(size)));
        return tile;
    }
    /**
     * The top of one of the app's own lists, as a computer's pages have it: the name of its tab large, a line under
     * it that says how things stand, and at the right what is done there.
     */
    LinearLayout head(String title, View under, View... actions) {
        LinearLayout head = row();
        LinearLayout titles = column();
        TextView name = bold(title, 29, INK);
        name.setIncludeFontPadding(false); name.setLineSpacing(0, 1f);
        titles.addView(name);
        if (under != null) titles.addView(under, below(5));
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
        return box;
    }
    /** A small action on a card: its icon on a quiet disc, with room around it for a thumb. */
    View discButton(int drawable, String says, Runnable action) {
        FrameLayout box = new FrameLayout(activity), disc = new FrameLayout(activity);
        disc.setBackground(shape(RAISED, 0, 18));
        disc.addView(icon(drawable, MUTED, 16), new FrameLayout.LayoutParams(dp(16), dp(16), Gravity.CENTER));
        box.addView(disc, new FrameLayout.LayoutParams(dp(36), dp(36), Gravity.CENTER));
        box.setContentDescription(says);
        box.setLayoutParams(new LinearLayout.LayoutParams(dp(48), dp(48)));
        press(box, action);
        return box;
    }
    View dot(int color, int size) {
        View dot = new View(activity);
        dot.setBackground(shape(color, 0, size));
        dot.setLayoutParams(new LinearLayout.LayoutParams(dp(size), dp(size)));
        return dot;
    }
    /** What a task is doing: its colour as a dot and as the text, on a wash of the same colour. */
    TextView pill(String label, int color) {
        TextView pill = bold(label, 11.5f, color);
        pill.setSingleLine(true); pill.setPadding(dp(8), dp(2), dp(9), dp(2)); pill.setBackground(shape(tint(color, 38), 0, 99));
        GradientDrawable mark = shape(color, 0, 6);
        mark.setBounds(0, 0, dp(6), dp(6));
        pill.setCompoundDrawables(mark, null, null, null); pill.setCompoundDrawablePadding(dp(5));
        return pill;
    }

    /** Choices on a sheet that rises from the bottom, within reach of a thumb. notes may hold null; danger marks one choice, or is -1. */
    // A sheet that rises from the bottom of the screen, in the app's look: its box with the grip, the title and a
    // line under it; and putting it on the screen.
    private LinearLayout sheetBox(String title, String note, int noteLines) {
        LinearLayout box = column();
        box.setPadding(dp(16), dp(8), dp(16), dp(14));
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
        dialog.setContentView(box);
        Window window = dialog.getWindow();
        // a sheet to write in comes with the keyboard and stays above it
        if (window != null && typing) window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_STATE_VISIBLE | android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        dialog.show();
        if (window == null) return;
        window.setBackgroundDrawable(new ColorDrawable(Color.TRANSPARENT));
        window.setLayout(-1, -2); window.setGravity(Gravity.BOTTOM);
    }
    /** A sheet with a box to write in and one button, which hands over what was written. */
    void write(String title, String note, String hint, String yes, java.util.function.Consumer<String> written) {
        final Dialog dialog = new Dialog(activity);
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        LinearLayout box = sheetBox(title, note, 3);
        final EditText field = field(hint, android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        field.setSingleLine(false); field.setMinLines(2); field.setMaxLines(6); field.setGravity(Gravity.TOP | Gravity.START);
        field.setBackground(shape(PANEL, 0, 14)); field.setPadding(dp(16), dp(13), dp(16), dp(13));
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
        // the choices as one group of rows, as the pages' sheets have them (web/ui/sheet.css); the one in use has a
        // tick ("当前使用" in its note), and the one that ends or deletes something stands apart below
        LinearLayout group = column(), apart = column();
        group.setBackground(shape(PANEL, 0, 16)); apart.setBackground(shape(PANEL, 0, 16));
        for (int i = 0; i < labels.length; i++) {
            String said = notes != null && notes[i] != null ? notes[i] : "";
            boolean current = said.startsWith("当前使用");
            if (current) said = said.replaceFirst("^当前使用( · )?", "");
            LinearLayout target = i == danger ? apart : group;
            if (target.getChildCount() > 0) { View line = new View(activity); line.setBackgroundColor(mix(LINE, PANEL, .8f)); LinearLayout.LayoutParams divider = new LinearLayout.LayoutParams(-1, dp(1)); divider.setMargins(dp(16), 0, 0, 0); target.addView(line, divider); }
            LinearLayout item = row();
            item.setMinimumHeight(dp(54)); item.setPadding(dp(16), dp(10), dp(14), dp(10));
            LinearLayout words = column();
            TextView name = text(labels[i], 16, i == danger ? BAD : current ? ACCENT : INK);
            if (current || i == danger) name.setTypeface(Typeface.DEFAULT_BOLD);
            if (i == danger) { name.setGravity(Gravity.CENTER); words.setGravity(Gravity.CENTER_HORIZONTAL); }
            words.addView(name);
            if (!said.isEmpty()) words.addView(text(said, 12.5f, MUTED), below(2));
            item.addView(words, new LinearLayout.LayoutParams(0, -2, 1));
            if (current) item.addView(icon(R.drawable.ic_check, ACCENT, 20));
            item.setContentDescription(labels[i] + (current ? "，当前使用" : ""));
            final int at = i;
            press(item, () -> { dialog.dismiss(); picked.accept(at); });
            target.addView(item);
        }
        if (group.getChildCount() > 0) box.addView(group, below(14));
        if (apart.getChildCount() > 0) box.addView(apart, below(group.getChildCount() > 0 ? 12 : 14));
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
            card.setBackground(shape(mix(danger ? BAD : ACCENT, BG, .1f), 0, 14));
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
        field.setBackground(shape(PANEL, 0, 14));
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
