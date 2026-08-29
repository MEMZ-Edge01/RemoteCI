import java.util.Properties

// 软件版本与协议版本独立，Release 工作流会用标签覆盖此默认值。
val releaseVersion = providers.gradleProperty("releaseVersion").orNull ?: "3.1.0"
// versionCode 由版本号推导（major*10000 + minor*100 + patch），随发布自然递增：
// 避免硬编码导致各版本 APK 携带相同 versionCode、依赖“同版本码覆盖”的脆弱行为。
val releaseVersionCode = releaseVersion
    .substringBefore("-")
    .split(".")
    .let { parts ->
        (parts.getOrNull(0)?.toIntOrNull() ?: 0) * 10_000 +
            (parts.getOrNull(1)?.toIntOrNull() ?: 0) * 100 +
            (parts.getOrNull(2)?.toIntOrNull() ?: 0)
    }

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// 签名信息从 wearos/keystore.properties（本机）或 CI 环境变量读取；
// 文件与密钥均不入库，缺失时 release 构建产物不签名（仅用于本地调试）。
val keystoreProperties = Properties().apply {
    val file = rootProject.file("keystore.properties")
    if (file.exists()) file.inputStream().use { load(it) }
}
val keystoreFile = System.getenv("ANDROID_KEYSTORE_FILE")?.let { rootProject.file(it) }
    ?: keystoreProperties.getProperty("storeFile")?.let { rootProject.file(it) }
val keystorePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
    ?: keystoreProperties.getProperty("storePassword")
val keystoreKeyAlias = System.getenv("ANDROID_KEY_ALIAS") ?: keystoreProperties.getProperty("keyAlias")
val keystoreKeyPassword = System.getenv("ANDROID_KEY_PASSWORD")
    ?: keystoreProperties.getProperty("keyPassword")

android {
    namespace = "com.remoteci.watch"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.remoteci.watch"
        minSdk = 30
        targetSdk = 37
        versionCode = releaseVersionCode
        versionName = releaseVersion
        testInstrumentationRunner =
            "com.remoteci.watch.NetworkSecurityPolicyInstrumentation"
    }

    signingConfigs {
        create("release") {
            if (keystoreFile != null && keystoreFile.exists()) {
                storeFile = keystoreFile
                storePassword = keystorePassword
                keyAlias = keystoreKeyAlias
                keyPassword = keystoreKeyPassword
            }
        }
    }

    buildTypes {
        release {
            // release 启用 R8 压缩；kotlinx.serialization 保留规则见 proguard-rules.pro。
            isMinifyEnabled = true
            signingConfig = if (keystoreFile != null && keystoreFile.exists()) {
                signingConfigs.getByName("release")
            } else {
                null
            }
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        buildConfig = true
        compose = true
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.foundation)
    implementation(libs.compose.material.icons.extended)
    implementation(libs.compose.material3)
    implementation(libs.wear.compose.material)
    implementation(libs.wear.compose.navigation)
    implementation(libs.activity.compose)
    implementation(libs.lifecycle.viewmodel.compose)
    implementation(libs.lifecycle.runtime.ktx)
    implementation(libs.wear)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.okhttp)
    testImplementation(kotlin("test"))
    testImplementation(libs.mockwebserver3)
    debugImplementation(libs.compose.ui.tooling)
}
