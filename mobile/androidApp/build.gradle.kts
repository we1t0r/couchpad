import java.util.Properties
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.compose.compiler)
}

android {
    namespace = "app.remotedesk.android"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "app.couchpad"
        minSdk = 26
        targetSdk = 36
        versionCode = 2
        versionName = "0.9.0"
    }

    // Ключ подписи для раздачи — вне проекта: %USERPROFILE%\.couchpad\signing.properties (storeFile, storePassword,
    // keyAlias, keyPassword). Его нельзя терять: без него приложение у людей не обновить поверх.
    val signingFile = File(System.getProperty("user.home"), ".couchpad/signing.properties")
    val signing = Properties().apply { if (signingFile.exists()) signingFile.inputStream().use(::load) }
    signingConfigs {
        if (signing.isNotEmpty()) create("release") {
            storeFile = File(signing.getProperty("storeFile"))
            storePassword = signing.getProperty("storePassword")
            keyAlias = signing.getProperty("keyAlias")
            keyPassword = signing.getProperty("keyPassword")
        }
    }

    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
        }
        release {
            // R8 пока выключен: на телефоне сборку с ним ещё не проверяли, а Ktor и сериализация чувствительны к нему.
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    // F-Droid: не вкладывать в APK зашифрованный блок с метаданными зависимостей.
    dependenciesInfo {
        includeInApk = false
        includeInBundle = false
    }
}

kotlin {
    compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
}

dependencies {
    implementation(project(":shared"))
    implementation(libs.androidx.activity.compose)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.zxing.embedded)
    implementation(libs.androidx.documentfile)
}
