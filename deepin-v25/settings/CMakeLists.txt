# 设置窗口 wwp-settings（Qt Quick）。由 factory/CMakeLists.txt 用 add_subdirectory 带进来一起编译。
# 提权助手 wwp-config、polkit 策略、.desktop、图标是现成文件，install.sh 直接拷。
find_package(Qt6 REQUIRED COMPONENTS Gui Quick QuickControls2)

qt_add_executable(wwp-settings
    main.cpp
    backend.h
    backend.cpp
)

qt_add_qml_module(wwp-settings
    URI WwpSettings
    VERSION 1.0
    QML_FILES
    Main.qml
    RESOURCES
    wwp-settings.png
)

target_link_libraries(wwp-settings PRIVATE Qt6::Gui Qt6::Quick Qt6::QuickControls2)

install(TARGETS wwp-settings RUNTIME DESTINATION "${CMAKE_INSTALL_BINDIR}")
