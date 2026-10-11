package io.github.kangwang42.remotecli;

import android.app.Activity;
import android.app.Dialog;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.GradientDrawable;
import android.text.TextUtils;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.MotionEvent;
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
    /** One row per skin in web/terminal/skins.js: background, panel, raised, line, ink, muted, accent, on accent, good, busy, bad. */
    private static final int[][] SKINS = {
        {0x1a1b26, 0x222436, 0x2c2f47, 0x2f334d, 0xc0caf5, 0x8189ad, 0x7aa2f7, 0x10121c, 0x9ece6a, 0xe0af68, 0xf7768e},
        {0x16181d, 0x1e2127, 0x2a2e37, 0x2c313a, 0xd7dae0, 0x7f8795, 0x61afef, 0x0f1114, 0x98c379, 0xe5c07b, 0xe06c75},
        {0x1e2326, 0x272e33, 0x323c41, 0x343f44, 0xd3c6aa, 0x859289, 0xa7c080, 0x1a2023, 0xa7c080, 0xdbbc7f, 0xe67e80},
        {0x1e1e2e, 0x262637, 0x313244, 0x313244, 0xcdd6f4, 0x8c8fa8, 0xcba6f7, 0x181825, 0xa6e3a1, 0xf9e2af, 0xf38ba8},
        {0xfbfbfc, 0xf0f1f4, 0xe4e6eb, 0xd5d8df, 0x2b2f3a, 0x666b78, 0x2f6fe4, 0xffffff, 0x3d8a3a, 0xa36a00, 0xc93c37},
        {0xfaf4ed, 0xf4ede4, 0xebe1d5, 0xddd3c6, 0x4a4566, 0x7a7089, 0x286983, 0xffffff, 0x3b7d5c, 0xb9781a, 0xb4506a}
    };
    static final String[] NAMES = {"night", "slate", "pine", "dusk", "paper", "dawn"};
    static final String[] TITLES = {"夜航", "墨岩", "松林", "暮紫", "纸白 · 明亮", "晨光 · 明亮"};
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
    /** A button: 0 the main action, 1 an ordinary one, 2 only text. A press dims it for a moment. */
    TextView button(String label, int kind, Runnable action) {
        TextView view = bold(label, 16, kind == 0 ? ON_ACCENT : kind == 1 ? INK : MUTED);
        view.setGravity(Gravity.CENTER); view.setMinHeight(dp(52)); view.setPadding(dp(18), 0, dp(18), 0);
        if (kind < 2) view.setBackground(shape(kind == 0 ? ACCENT : PANEL, kind == 0 ? 0 : LINE, 16));
        press(view, action);
        return view;
    }
    void press(View view, Runnable action) {
        view.setClickable(true); view.setFocusable(true);
        view.setOnTouchListener((v, event) -> {
            int what = event.getActionMasked();
            if (what == MotionEvent.ACTION_DOWN) v.setAlpha(.6f);
            else if (what == MotionEvent.ACTION_UP || what == MotionEvent.ACTION_CANCEL) v.setAlpha(1f);
            return false;
        });
        view.setOnClickListener(v -> action.run());
    }
    LinearLayout.LayoutParams below(int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.topMargin = dp(top);
        return params;
    }
    LinearLayout column() { LinearLayout column = new LinearLayout(activity); column.setOrientation(LinearLayout.VERTICAL); return column; }
    LinearLayout row() { LinearLayout row = new LinearLayout(activity); row.setGravity(Gravity.CENTER_VERTICAL); return row; }
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
    /** An icon that is a button of its own, large enough for a thumb. */
    View iconButton(int drawable, String says, Runnable action) {
        FrameLayout box = new FrameLayout(activity);
        box.addView(icon(drawable, MUTED, 22), new FrameLayout.LayoutParams(dp(22), dp(22), Gravity.CENTER));
        box.setContentDescription(says);
        box.setLayoutParams(new LinearLayout.LayoutParams(dp(44), dp(44)));
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
        box.setLayoutParams(new LinearLayout.LayoutParams(dp(44), dp(44)));
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
        box.setPadding(dp(18), dp(10), dp(18), dp(14));
        GradientDrawable back = new GradientDrawable();
        back.setColor(PANEL); back.setCornerRadii(new float[]{dp(22), dp(22), dp(22), dp(22), 0, 0, 0, 0});
        box.setBackground(back);
        View grip = new View(activity);
        grip.setBackground(shape(LINE, 0, 2));
        LinearLayout.LayoutParams middle = new LinearLayout.LayoutParams(dp(40), dp(4));
        middle.gravity = Gravity.CENTER_HORIZONTAL; middle.bottomMargin = dp(14);
        box.addView(grip, middle);
        box.addView(bold(title, 18, INK));
        if (note != null && !note.isEmpty()) {
            TextView under = text(note, 13.5f, MUTED);
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
        field.setBackground(shape(BG, LINE, 14)); field.setPadding(dp(16), dp(13), dp(16), dp(13));
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
        for (int i = 0; i < labels.length; i++) {
            LinearLayout item = column();
            item.setGravity(Gravity.CENTER_VERTICAL); item.setMinimumHeight(dp(54)); item.setPadding(dp(16), dp(9), dp(16), dp(9));
            item.setBackground(shape(RAISED, LINE, 14));
            item.addView(bold(labels[i], 15.5f, i == danger ? BAD : INK));
            if (notes != null && notes[i] != null && !notes[i].isEmpty()) item.addView(text(notes[i], 12.5f, MUTED));
            final int at = i;
            press(item, () -> { dialog.dismiss(); picked.accept(at); });
            box.addView(item, below(i == 0 ? 14 : 8));
        }
        box.addView(button("取消", 2, dialog::dismiss), below(4));
        raise(dialog, box, false);
    }
}
