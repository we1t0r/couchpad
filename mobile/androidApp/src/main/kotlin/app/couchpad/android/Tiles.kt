package app.couchpad.android

import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import app.couchpad.core.PowerAction
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Плитка в шторке быстрых настроек: одно нажатие — одна команда на последний компьютер.
 * [action] == null — включить компьютер по сети.
 */
abstract class PowerTile(private val action: PowerAction?) : TileService() {
    override fun onStartListening() {
        setActive(false)
    }

    override fun onClick() {
        // Подсвечиваем плитку, пока команда идёт, чтобы было видно, что нажатие сработало.
        setActive(true)
        appScope.launch {
            QuickActions.runAndToast(applicationContext, action)
            withContext(Dispatchers.Main) { setActive(false) }
        }
    }

    private fun setActive(active: Boolean) {
        val tile = qsTile ?: return
        tile.state = if (active) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        tile.updateTile()
    }
}

class MonitorsOffTile : PowerTile(PowerAction.MonitorsOff)

class MonitorsOnTile : PowerTile(PowerAction.MonitorsOn)

class LockTile : PowerTile(PowerAction.Lock)

class WakeTile : PowerTile(null)
