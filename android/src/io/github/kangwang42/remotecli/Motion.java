package io.github.kangwang42.remotecli;

import android.animation.Animator;
import android.animation.AnimatorSet;
import android.animation.LayoutTransition;
import android.animation.ObjectAnimator;
import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.DashPathEffect;
import android.graphics.Paint;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.animation.Interpolator;
import android.view.animation.LinearInterpolator;
import android.view.animation.OvershootInterpolator;
import android.view.animation.PathInterpolator;
import android.widget.TextView;
import java.util.WeakHashMap;

/**
 * Lightweight native motion helpers for the app's screens: tactile spring presses, quota bar reveals,
 * animated KPI counters, live status pulses, smooth container transitions, and the animated
 * collaboration pipeline connector. All animations respect Android's animator scale and stop on detach.
 */
final class Motion {
    static final Interpolator EASE_OUT = new PathInterpolator(0.22f, 1f, 0.36f, 1f);
    private static final Interpolator SPRING = new OvershootInterpolator(1.6f);
    private static final WeakHashMap<TextView, ValueAnimator> COUNTERS = new WeakHashMap<>();

    static boolean enabled() {
        return ValueAnimator.areAnimatorsEnabled();
    }

    /** Tactile press-down shrink and spring-back without intercepting click or ripple handling. */
    static void springPress(View view, float downScale) {
        view.setOnTouchListener((v, event) -> {
            if (!enabled() || !v.isEnabled()) return false;
            int action = event.getActionMasked();
            if (action == MotionEvent.ACTION_DOWN) {
                v.animate().cancel();
                v.animate().scaleX(downScale).scaleY(downScale).setDuration(85).setInterpolator(EASE_OUT).start();
            } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
                v.animate().cancel();
                v.animate().scaleX(1f).scaleY(1f).setDuration(210).setInterpolator(SPRING).start();
            }
            return false;
        });
    }

    /** Reveals a left-anchored progress bar from 0 to its laid-out width with gentle stagger. */
    static void revealBar(View bar, int index) {
        if (!enabled()) return;
        bar.setPivotX(0f);
        bar.setScaleX(0f);
        bar.animate().cancel();
        bar.animate().scaleX(1f).setDuration(420).setStartDelay(Math.min(Math.max(0, index), 6) * 38L)
            .setInterpolator(EASE_OUT).start();
    }

    /** Gentle entrance lift and fade for cards or newly revealed blocks. */
    static void riseIn(View view, float distancePx, int delayMs) {
        if (!enabled()) return;
        view.setAlpha(0f);
        view.setTranslationY(distancePx);
        view.animate().cancel();
        view.animate().alpha(1f).translationY(0f).setDuration(240).setStartDelay(Math.max(0, delayMs))
            .setInterpolator(EASE_OUT).start();
    }

    /** Crisp bounce on the selected bottom-tab icon. */
    static void popTab(View target) {
        if (!enabled()) return;
        target.setScaleX(0.84f);
        target.setScaleY(0.84f);
        target.animate().cancel();
        target.animate().scaleX(1f).scaleY(1f).setDuration(260).setInterpolator(new OvershootInterpolator(2.3f)).start();
    }

    /** Single 360-degree clockwise spin for refresh buttons. */
    static void spinOnce(View target) {
        if (!enabled() || target == null) return;
        target.animate().cancel();
        target.setRotation(0f);
        target.animate().rotation(360f).setDuration(420).setInterpolator(EASE_OUT).start();
    }

    /** Smoothly counts a numeric KPI TextView to target when its displayed value changes. */
    static void countTo(TextView view, long target) {
        if (view == null) return;
        ValueAnimator previous = COUNTERS.remove(view);
        if (previous != null) previous.cancel();
        long start = parseLong(view.getText());
        if (!enabled() || start == target) {
            view.setText(String.valueOf(target));
            return;
        }
        long from = start < 0 ? 0 : start;
        ValueAnimator anim = ValueAnimator.ofFloat(from, target);
        anim.setDuration(300);
        anim.setInterpolator(EASE_OUT);
        anim.addUpdateListener(a -> view.setText(String.valueOf(Math.round((Float) a.getAnimatedValue()))));
        anim.addListener(new android.animation.AnimatorListenerAdapter() {
            @Override public void onAnimationEnd(Animator animation) {
                COUNTERS.remove(view);
                view.setText(String.valueOf(target));
            }
        });
        COUNTERS.put(view, anim);
        anim.start();
    }

    private static long parseLong(CharSequence text) {
        if (text == null) return -1;
        String s = text.toString().trim();
        if (s.isEmpty() || !s.matches("\\d+")) return -1;
        try { return Long.parseLong(s); } catch (NumberFormatException ignored) { return -1; }
    }

    /**
     * Gentle breathing pulse on a live indicator dot or halo ring.
     * Automatically starts when attached to a window and cancels on detach.
     */
    static void pulse(View target, float minAlpha, float maxScale, long durationMs) {
        if (target == null || !enabled()) return;
        ObjectAnimator alpha = ObjectAnimator.ofFloat(target, View.ALPHA, 1f, minAlpha, 1f);
        alpha.setRepeatCount(ValueAnimator.INFINITE);
        alpha.setDuration(durationMs);
        AnimatorSet set = new AnimatorSet();
        if (maxScale > 1.01f) {
            ObjectAnimator sx = ObjectAnimator.ofFloat(target, View.SCALE_X, 1f, maxScale, 1f);
            ObjectAnimator sy = ObjectAnimator.ofFloat(target, View.SCALE_Y, 1f, maxScale, 1f);
            sx.setRepeatCount(ValueAnimator.INFINITE);
            sy.setRepeatCount(ValueAnimator.INFINITE);
            sx.setDuration(durationMs);
            sy.setDuration(durationMs);
            set.playTogether(alpha, sx, sy);
        } else {
            set.play(alpha);
        }
        set.setInterpolator(EASE_OUT);
        target.addOnAttachStateChangeListener(new View.OnAttachStateChangeListener() {
            @Override public void onViewAttachedToWindow(View v) { if (enabled() && !set.isStarted()) set.start(); }
            @Override public void onViewDetachedFromWindow(View v) {
                set.cancel();
                v.setAlpha(1f); v.setScaleX(1f); v.setScaleY(1f);
            }
        });
        if (target.isAttachedToWindow()) set.start();
    }

    /** Enables smooth expand/collapse transitions inside a card without disturbing ancestor scrolling. */
    static void smoothLayout(ViewGroup group) {
        if (group == null || !enabled()) return;
        LayoutTransition transition = new LayoutTransition();
        transition.enableTransitionType(LayoutTransition.CHANGING);
        transition.setAnimateParentHierarchy(false);
        transition.setDuration(200);
        transition.setInterpolator(LayoutTransition.CHANGING, EASE_OUT);
        transition.setInterpolator(LayoutTransition.APPEARING, EASE_OUT);
        transition.setInterpolator(LayoutTransition.DISAPPEARING, EASE_OUT);
        group.setLayoutTransition(transition);
    }

    /** Animated dashed connector with a travelling accent pulse for the collaboration hero pipeline. */
    static final class FlowLineView extends View {
        private final Paint dashPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint glowPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint beadPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final float strokePx, dashPx, gapPx, beadRadius, glowRadius;
        private final long phaseOffsetMs;
        private float progress;
        private ValueAnimator runner;

        FlowLineView(Context context, int dashColor, int accentColor, float density, long phaseOffsetMs) {
            super(context);
            this.strokePx = Math.max(1.5f, density * 1.8f);
            this.dashPx = density * 5f;
            this.gapPx = density * 4f;
            this.beadRadius = density * 2.6f;
            this.glowRadius = density * 5.2f;
            this.phaseOffsetMs = phaseOffsetMs;
            dashPaint.setStyle(Paint.Style.STROKE);
            dashPaint.setStrokeWidth(strokePx);
            dashPaint.setStrokeCap(Paint.Cap.ROUND);
            dashPaint.setColor(dashColor);
            glowPaint.setStyle(Paint.Style.FILL);
            glowPaint.setColor(Kit.tint(accentColor, 64));
            beadPaint.setStyle(Paint.Style.FILL);
            beadPaint.setColor(accentColor);
        }

        @Override protected void onAttachedToWindow() {
            super.onAttachedToWindow();
            if (!enabled()) return;
            if (runner != null) runner.cancel();
            runner = ValueAnimator.ofFloat(0f, 1f);
            runner.setDuration(1700);
            runner.setRepeatCount(ValueAnimator.INFINITE);
            runner.setInterpolator(new LinearInterpolator());
            if (phaseOffsetMs > 0) runner.setCurrentPlayTime(phaseOffsetMs);
            runner.addUpdateListener(a -> {
                progress = (Float) a.getAnimatedValue();
                invalidate();
            });
            runner.start();
        }

        @Override protected void onDetachedFromWindow() {
            if (runner != null) { runner.cancel(); runner = null; }
            super.onDetachedFromWindow();
        }

        @Override protected void onDraw(Canvas canvas) {
            int w = getWidth(), h = getHeight();
            if (w <= 0 || h <= 0) return;
            float cy = h * 0.5f;
            float phase = enabled() ? -(dashPx + gapPx) * 2f * progress : 0f;
            dashPaint.setPathEffect(new DashPathEffect(new float[]{dashPx, gapPx}, phase));
            canvas.drawLine(0f, cy, w, cy, dashPaint);
            if (!enabled()) return;
            float x = w * progress;
            float edge = Math.min(1f, Math.min(progress / 0.16f, (1f - progress) / 0.16f));
            glowPaint.setAlpha(Math.round(70 * edge));
            beadPaint.setAlpha(Math.round(230 * edge));
            canvas.drawCircle(x, cy, glowRadius, glowPaint);
            canvas.drawCircle(x, cy, beadRadius, beadPaint);
        }
    }

    private Motion() { }
}
