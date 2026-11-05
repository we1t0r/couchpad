package app.couchpad.android

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.widget.RemoteViews
import app.couchpad.core.PcStore
import app.couchpad.core.PowerAction
import kotlinx.coroutines.launch

/** Виджет на рабочий стол: «Включить ПК», «Выключить мониторы», «Включить мониторы», «Заблокировать». */
class RemoteWidget : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        val views = views(context)
        ids.forEach { manager.updateAppWidget(it, views) }
    }

    override fun onReceive(context: Context, intent: Intent) {
        val name = intent.getStringExtra(EXTRA_ACTION)
        if (intent.action != ACTION_RUN || name == null) {
            super.onReceive(context, intent)
            return
        }
        // WAKE — включить по сети, иначе команда питания.
        val action = PowerAction.entries.firstOrNull { it.name == name }
        // goAsync даёт широковещательному приёмнику до ~10 с на работу в фоне.
        val pending = goAsync()
        appScope.launch {
            try {
                QuickActions.runAndToast(context.applicationContext, action)
            } finally {
                refresh(context)
                pending.finish()
            }
        }
    }

    companion object {
        private const val ACTION_RUN = "app.couchpad.widget.RUN"
        private const val EXTRA_ACTION = "action"
        private const val WAKE = "WAKE"

        /** Обновить подпись (имя компьютера) на всех виджетах. */
        fun refresh(context: Context) {
            val manager = AppWidgetManager.getInstance(context)
            val ids = manager.getAppWidgetIds(ComponentName(context, RemoteWidget::class.java))
            if (ids.isNotEmpty()) manager.updateAppWidget(ids, views(context))
        }

        private fun views(context: Context): RemoteViews {
            val pc = PcStore(context.settingsStorage()).lastUsed()
            return RemoteViews(context.packageName, R.layout.widget_remote).apply {
                setTextViewText(R.id.widget_title, pc?.name ?: context.getString(R.string.qa_no_pc_short))
                setOnClickPendingIntent(R.id.widget_title, openApp(context))
                setOnClickPendingIntent(R.id.widget_monitors_off, run(context, PowerAction.MonitorsOff))
                setOnClickPendingIntent(R.id.widget_monitors_on, run(context, PowerAction.MonitorsOn))
                setOnClickPendingIntent(R.id.widget_lock, run(context, PowerAction.Lock))
                setOnClickPendingIntent(R.id.widget_wake, run(context, null))
            }
        }

        private fun run(context: Context, action: PowerAction?): PendingIntent {
            val intent = Intent(context, RemoteWidget::class.java)
                .setAction(ACTION_RUN)
                .putExtra(EXTRA_ACTION, action?.name ?: WAKE)
            return PendingIntent.getBroadcast(
                context,
                action?.ordinal ?: 99,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
        }

        private fun openApp(context: Context): PendingIntent = PendingIntent.getActivity(
            context,
            100,
            Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }
}
