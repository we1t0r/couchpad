package app.remotedesk.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/** Цвет «в сети / всё хорошо». */
val Green = Color(0xFF1D9E75)

private val Light = lightColorScheme(
    primary = Color(0xFF2563EB), onPrimary = Color.White,
    primaryContainer = Color(0xFFDCE6FF), onPrimaryContainer = Color(0xFF0B2A6B),
    secondary = Color(0xFF1D9E75), onSecondary = Color.White,
    // Мягкие кнопки и выбранная вкладка — в тон синему, зелёный остаётся только для «в сети».
    secondaryContainer = Color(0xFFDCE6FF), onSecondaryContainer = Color(0xFF0B2A6B),
    tertiary = Color(0xFFB45309), onTertiary = Color.White,
    tertiaryContainer = Color(0xFFFDE9CC), onTertiaryContainer = Color(0xFF4A2300),
    background = Color(0xFFF6F7FB), onBackground = Color(0xFF161A23),
    surface = Color(0xFFF6F7FB), onSurface = Color(0xFF161A23),
    surfaceVariant = Color(0xFFE3E7F0), onSurfaceVariant = Color(0xFF596173),
    surfaceContainerLowest = Color.White, surfaceContainerLow = Color(0xFFF0F2F8),
    surfaceContainer = Color(0xFFFFFFFF), surfaceContainerHigh = Color(0xFFE9ECF4),
    surfaceContainerHighest = Color(0xFFE1E5EE),
    outline = Color(0xFF8A91A1), outlineVariant = Color(0xFFD9DDE6),
    error = Color(0xFFDC2626), onError = Color.White,
    errorContainer = Color(0xFFFDE2E2), onErrorContainer = Color(0xFF7F1D1D),
    surfaceTint = Color(0xFF2563EB),
)

private val Dark = darkColorScheme(
    primary = Color(0xFF8AB4FF), onPrimary = Color(0xFF0B1F44),
    primaryContainer = Color(0xFF1F3A70), onPrimaryContainer = Color(0xFFDCE6FF),
    secondary = Color(0xFF6FD3B0), onSecondary = Color(0xFF00382A),
    secondaryContainer = Color(0xFF243553), onSecondaryContainer = Color(0xFFDCE6FF),
    tertiary = Color(0xFFF6B861), onTertiary = Color(0xFF3F2200),
    tertiaryContainer = Color(0xFF573A12), onTertiaryContainer = Color(0xFFFDE9CC),
    background = Color(0xFF0E1116), onBackground = Color(0xFFE4E7EE),
    surface = Color(0xFF0E1116), onSurface = Color(0xFFE4E7EE),
    surfaceVariant = Color(0xFF2A303B), onSurfaceVariant = Color(0xFFA6AEBD),
    surfaceContainerLowest = Color(0xFF090B0F), surfaceContainerLow = Color(0xFF14181F),
    surfaceContainer = Color(0xFF181C24), surfaceContainerHigh = Color(0xFF212733),
    surfaceContainerHighest = Color(0xFF2A313E),
    outline = Color(0xFF6B7385), outlineVariant = Color(0xFF2E3542),
    error = Color(0xFFFF8A80), onError = Color(0xFF5C0A0A),
    errorContainer = Color(0xFF5C1B1B), onErrorContainer = Color(0xFFFFDAD6),
    surfaceTint = Color(0xFF8AB4FF),
)

@Composable
fun RemoteDeskTheme(dark: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = if (dark) Dark else Light,
        shapes = Shapes(
            extraSmall = RoundedCornerShape(8.dp),
            small = RoundedCornerShape(12.dp),
            medium = RoundedCornerShape(16.dp),
            large = RoundedCornerShape(20.dp),
            extraLarge = RoundedCornerShape(28.dp),
        ),
        content = content,
    )
}

/** Заголовок группы на экране: мелкий, приглушённый, не спорит с содержимым. */
@Composable
fun SectionTitle(text: String, modifier: Modifier = Modifier) {
    Text(
        text,
        style = MaterialTheme.typography.titleSmall,
        fontWeight = FontWeight.SemiBold,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = modifier.padding(start = 4.dp, top = 8.dp, bottom = 2.dp),
    )
}

/** Карточка-группа: одинаковый фон и скругление на всех экранах. */
@Composable
fun Panel(modifier: Modifier = Modifier, onClick: (() -> Unit)? = null, content: @Composable ColumnScope.() -> Unit) {
    val colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer)
    val shape = MaterialTheme.shapes.large
    val inner: @Composable ColumnScope.() -> Unit = {
        Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp), content = content)
    }
    if (onClick != null) Card(onClick = onClick, modifier = modifier.fillMaxWidth(), shape = shape, colors = colors, content = inner)
    else Card(modifier = modifier.fillMaxWidth(), shape = shape, colors = colors, content = inner)
}

/** Значок в цветном скруглённом квадрате — как у приложений в настройках телефона. */
@Composable
fun IconBadge(
    icon: ImageVector,
    tint: Color = MaterialTheme.colorScheme.primary,
    container: Color = MaterialTheme.colorScheme.primaryContainer,
    size: Dp = 44.dp,
) {
    Box(Modifier.size(size).background(container, RoundedCornerShape(size * 0.3f)), contentAlignment = Alignment.Center) {
        Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(size * 0.55f))
    }
}

/** Пояснение мелким приглушённым шрифтом. */
@Composable
fun Hint(text: String, modifier: Modifier = Modifier) {
    Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = modifier)
}
