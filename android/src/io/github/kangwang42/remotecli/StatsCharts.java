package io.github.kangwang42.remotecli;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.DashPathEffect;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;
import java.util.ArrayList;
import java.util.List;
import java.util.function.IntConsumer;

/**
 * Custom Canvas visual components for the Android native daily statistics card:
 * an interactive 7-day multi-AI stacked bar chart with a dashed period average reference line.
 */
final class StatsCharts {
    static final class DailyBarsView extends View {
        private final Paint barPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint trackPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint pickPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint avgLinePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint avgTextPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint labelPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint labelSelectedPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF rect = new RectF();
        private final Path clipPath = new Path();
        private final float density;

        private int colorClaude, colorCodex, colorAgy, colorFallback, colorTrack, colorPickBg, colorAvg, colorMuted, colorInk;
        private List<StatsRollup.DayPoint> days = new ArrayList<>();
        private double avgPrompts;
        private int selected = -1;
        private IntConsumer onPick;
        private float reveal = 1f;
        private boolean hasAnimated;
        private ValueAnimator revealAnim;

        DailyBarsView(Context context, Kit kit) {
            super(context);
            this.density = context.getResources().getDisplayMetrics().density;
            applyTheme(kit);
            setClickable(true);
            setContentDescription("近 7 天每日提问趋势图");
        }

        void applyTheme(Kit kit) {
            boolean light = Kit.light(kit.BG);
            colorClaude = light ? 0xffd06135 : 0xfff0936b;
            colorCodex = light ? 0xff2563eb : 0xff6ea8fe;
            colorAgy = light ? 0xff7c3aed : 0xffb392f0;
            colorFallback = kit.ACCENT;
            colorTrack = Kit.tint(kit.MUTED, light ? 26 : 36);
            colorPickBg = kit.accentSoft();
            colorAvg = Kit.tint(kit.MUTED, light ? 120 : 140);
            colorMuted = kit.MUTED;
            colorInk = kit.INK;

            trackPaint.setStyle(Paint.Style.FILL);
            trackPaint.setColor(colorTrack);

            pickPaint.setStyle(Paint.Style.FILL);
            pickPaint.setColor(colorPickBg);

            avgLinePaint.setStyle(Paint.Style.STROKE);
            avgLinePaint.setStrokeWidth(Math.max(1f, density));
            avgLinePaint.setColor(colorAvg);
            avgLinePaint.setPathEffect(new DashPathEffect(new float[] { density * 4f, density * 3.5f }, 0));

            avgTextPaint.setColor(colorMuted);
            avgTextPaint.setTextSize(10f * density);
            avgTextPaint.setTextAlign(Paint.Align.RIGHT);

            labelPaint.setColor(colorMuted);
            labelPaint.setTextSize(11f * density);
            labelPaint.setTextAlign(Paint.Align.CENTER);

            labelSelectedPaint.setColor(colorInk);
            labelSelectedPaint.setTextSize(11f * density);
            labelSelectedPaint.setTypeface(Typeface.create(Typeface.DEFAULT, Typeface.BOLD));
            labelSelectedPaint.setTextAlign(Paint.Align.CENTER);
        }

        void setOnPick(IntConsumer listener) { this.onPick = listener; }

        void setData(List<StatsRollup.DayPoint> nextDays, double avg, int selectedIndex) {
            this.days = nextDays == null ? new ArrayList<>() : new ArrayList<>(nextDays);
            this.avgPrompts = avg;
            this.selected = days.isEmpty() ? -1 : Math.max(0, Math.min(days.size() - 1, selectedIndex));
            if (!hasAnimated && !days.isEmpty() && Motion.enabled()) {
                hasAnimated = true;
                if (revealAnim != null) revealAnim.cancel();
                reveal = 0f;
                revealAnim = ValueAnimator.ofFloat(0f, 1f);
                revealAnim.setDuration(420);
                revealAnim.setInterpolator(Motion.EASE_OUT);
                revealAnim.addUpdateListener(a -> {
                    reveal = (Float) a.getAnimatedValue();
                    invalidate();
                });
                revealAnim.start();
            } else {
                reveal = 1f;
                invalidate();
            }
        }

        void setSelected(int index) {
            if (days.isEmpty()) return;
            int clamped = Math.max(0, Math.min(days.size() - 1, index));
            if (clamped != selected) {
                selected = clamped;
                invalidate();
            }
        }

        @Override protected void onDetachedFromWindow() {
            if (revealAnim != null) { revealAnim.cancel(); revealAnim = null; }
            super.onDetachedFromWindow();
        }

        @Override public boolean onTouchEvent(MotionEvent event) {
            if (days.isEmpty()) return super.onTouchEvent(event);
            if (event.getActionMasked() == MotionEvent.ACTION_UP) {
                float w = getWidth();
                if (w > 0) {
                    int idx = (int) (event.getX() / (w / days.size()));
                    idx = Math.max(0, Math.min(days.size() - 1, idx));
                    if (idx != selected) {
                        selected = idx;
                        performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK);
                        invalidate();
                        if (onPick != null) onPick.accept(idx);
                    }
                }
            }
            return true;
        }

        @Override protected void onDraw(Canvas canvas) {
            int n = days.size();
            if (n == 0) return;
            float w = getWidth(), h = getHeight();
            if (w <= 0 || h <= 0) return;

            float topPad = 14f * density;
            float labelArea = 22f * density;
            float chartBottom = h - labelArea;
            float chartHeight = Math.max(12f * density, chartBottom - topPad);

            long maxVal = 1;
            for (StatsRollup.DayPoint d : days) if (d.prompts > maxVal) maxVal = d.prompts;

            // Subtle dashed period average reference line
            if (avgPrompts > 0.25) {
                float avgY = chartBottom - (float) (Math.min(1.0, avgPrompts / maxVal) * chartHeight);
                canvas.drawLine(0f, avgY, w, avgY, avgLinePaint);
                String avgTag = "日均 " + (avgPrompts >= 10 ? String.valueOf(Math.round(avgPrompts)) : String.format(java.util.Locale.US, "%.1f", avgPrompts));
                canvas.drawText(avgTag, w - 2f * density, Math.max(topPad - 2f * density, avgY - 4f * density), avgTextPaint);
            }

            float colW = w / n;
            float barW = Math.min(22f * density, colW * 0.46f);
            float radius = Math.min(6f * density, barW * 0.45f);
            float minBarH = 3.5f * density;

            for (int i = 0; i < n; i++) {
                StatsRollup.DayPoint day = days.get(i);
                float cx = colW * (i + 0.5f);
                float left = cx - barW * 0.5f;
                float right = cx + barW * 0.5f;
                boolean isSelected = i == selected;
                boolean isToday = i == n - 1;

                if (isSelected) {
                    float pillW = Math.min(colW - 4f * density, barW + 14f * density);
                    rect.set(cx - pillW * 0.5f, topPad - 6f * density, cx + pillW * 0.5f, h - 1f * density);
                    canvas.drawRoundRect(rect, 10f * density, 10f * density, pickPaint);
                }

                // Subtle background rail
                rect.set(left, topPad, right, chartBottom);
                canvas.drawRoundRect(rect, radius, radius, trackPaint);

                if (day.prompts > 0) {
                    float totalH = Math.max(minBarH, (float) ((double) day.prompts / maxVal) * chartHeight * reveal);
                    float barTop = chartBottom - totalH;

                    clipPath.reset();
                    rect.set(left, barTop, right, chartBottom);
                    clipPath.addRoundRect(rect, radius, radius, Path.Direction.CW);

                    int save = canvas.save();
                    canvas.clipPath(clipPath);

                    long cP = day.toolPrompts("claude");
                    long xP = day.toolPrompts("codex");
                    long aP = day.toolPrompts("agy");
                    long oP = day.otherPrompts();
                    int alpha = isSelected ? 255 : 210;

                    if (cP + xP + aP == 0) {
                        barPaint.setColor(Kit.tint(colorFallback, alpha));
                        canvas.drawRect(left, barTop, right, chartBottom, barPaint);
                    } else {
                        float yCursor = chartBottom;
                        yCursor = drawSegment(canvas, left, right, yCursor, totalH, day.prompts, cP, Kit.tint(colorClaude, alpha));
                        yCursor = drawSegment(canvas, left, right, yCursor, totalH, day.prompts, xP, Kit.tint(colorCodex, alpha));
                        yCursor = drawSegment(canvas, left, right, yCursor, totalH, day.prompts, aP, Kit.tint(colorAgy, alpha));
                        if (oP > 0) drawSegment(canvas, left, right, yCursor, totalH, day.prompts, oP, Kit.tint(colorFallback, alpha));
                    }
                    canvas.restoreToCount(save);
                } else {
                    barPaint.setColor(Kit.tint(colorMuted, isSelected ? 120 : 65));
                    rect.set(left, chartBottom - minBarH, right, chartBottom);
                    canvas.drawRoundRect(rect, radius, radius, barPaint);
                }

                String lbl = StatsRollup.shortWeekday(day.date, isToday);
                Paint tp = isSelected ? labelSelectedPaint : labelPaint;
                canvas.drawText(lbl, cx, h - 6f * density, tp);
            }
        }

        private float drawSegment(Canvas canvas, float left, float right, float yBottom, float totalH, long totalVal, long segVal, int color) {
            if (segVal <= 0 || totalVal <= 0) return yBottom;
            float segH = totalH * ((float) segVal / (float) totalVal);
            float yTop = yBottom - segH;
            barPaint.setColor(color);
            canvas.drawRect(left, yTop, right, yBottom, barPaint);
            return yTop;
        }
    }

    private StatsCharts() { }
}
