# RemoteDesk

Телефон как пульт от компьютера на Windows: тачпад, клавиатура, файлы в обе стороны, буфер обмена, питание и мониторы, Wake-on-LAN и просмотр экрана через интернет.

Открытый код, без рекламы и слежки, живёт на пожертвованиях.

- `docs/SPEC.md` — спецификация продукта и этапы.
- `agent/` — программа для Windows (C# / .NET 10). *Этап M0.*
- `mobile/` — приложение (Kotlin Multiplatform + Compose Multiplatform): сначала Android, потом iPhone. *Этап M0.*
- `protocol/` — описание сообщений между приложением и агентом. *Этап M0.*
- `relay/` — сервер связи для доступа через интернет. *Этап M5.*

## Сборка

Агент (нужен .NET 10 SDK):

```bash
dotnet build agent/RemoteDesk.Agent.slnx
```

Приложение для Android (нужны JDK 17+ и Android SDK 36; путь к SDK — в `ANDROID_HOME` или `mobile/local.properties`):

```bash
cd mobile && ./gradlew :androidApp:assembleDebug
```

Тест совместимости приложения с настоящим агентом (Windows, агент должен быть собран): `./gradlew :shared:jvmTest`. Агент запускается в режиме `--loopback` и слушает только 127.0.0.1.

Лицензия: GPL-3.0 (агент и приложение), AGPL-3.0 (сервер связи).
