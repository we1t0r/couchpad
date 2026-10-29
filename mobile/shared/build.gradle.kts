import org.jetbrains.kotlin.gradle.ExperimentalKotlinGradlePluginApi
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.kotlin.multiplatform)
    alias(libs.plugins.android.library)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.compose.multiplatform)
    alias(libs.plugins.compose.compiler)
}

kotlin {
    androidTarget {
        compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
    }
    // JVM нужен для тестов против настоящего агента (и когда-нибудь для клиента на ПК).
    jvm {
        compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
    }
    // Позже: iosArm64(), iosSimulatorArm64().

    // Общий код для Android и JVM: сеть на OkHttp с закреплением сертификата.
    @OptIn(ExperimentalKotlinGradlePluginApi::class)
    applyDefaultHierarchyTemplate {
        common {
            group("jvmCommon") {
                withAndroidTarget()
                withJvm()
            }
        }
    }

    sourceSets {
        commonMain.dependencies {
            api(compose.runtime)
            api(libs.multiplatform.settings)
            implementation(compose.foundation)
            implementation(compose.material3)
            implementation(compose.components.resources)
            implementation(libs.material.icons.core)
            implementation(libs.kotlinx.coroutines.core)
            implementation(libs.kotlinx.serialization.json)
            implementation(libs.ktor.client.core)
        }
        commonTest.dependencies {
            implementation(kotlin("test"))
            implementation(libs.kotlinx.coroutines.test)
            implementation(libs.multiplatform.settings.test)
        }
        getByName("jvmCommonMain").dependencies {
            implementation(libs.ktor.client.okhttp)
        }
        androidMain.dependencies {
            implementation(libs.kotlinx.coroutines.android)
            implementation(libs.androidx.activity.compose)
        }
    }
}

android {
    namespace = "app.remotedesk.shared"
    compileSdk = 36
    buildToolsVersion = "36.0.0"
    defaultConfig { minSdk = 26 }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

compose.resources {
    packageOfResClass = "app.remotedesk.resources"
}

// Тест совместимости запускает собранный агент для Windows, если он есть.
tasks.named<Test>("jvmTest") {
    val agent = rootProject.projectDir.resolve("../agent/src/RemoteDesk.Agent/bin/Debug/net10.0-windows/RemoteDesk.exe")
    systemProperty("rdesk.agentExe", agent.canonicalPath)
    testLogging { events("passed", "skipped", "failed"); showStandardStreams = true }
}
