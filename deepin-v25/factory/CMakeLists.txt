# 基于 treeland 0.11.0 的 wallpaper-factory（linuxdeepin/treeland，标签 0.11.0，目录 wallpaper-factory/）改出来的独立工程。
# 和原版的差别只有 WWP 支持：wwpcontroller.*、WwpScene.qml、main.cpp 的 --snapshot 和 SIGHUP 原地重启，以及
# treelandwallpapernotifierclient.cpp 里标了 “WWP” 的几处。图片/视频壁纸的逻辑原样保留。
# treeland 升级后要拿新标签的 wallpaper-factory 重新合一遍，协议可能变。
cmake_minimum_required(VERSION 3.18)
project(wwp-treeland-wallpaper-factory LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)
include(GNUInstallDirs)

set(BIN_NAME treeland-wallpaper-factory)

find_package(PkgConfig REQUIRED)
find_package(Qt6 REQUIRED COMPONENTS WaylandClient Quick)
# 和 treeland 顶层一致。少了它 QML 模块会放在 qrc:/com/... 而不是 qrc:/qt/qml/...，
# 装到 /usr/local/bin 后 loadFromModule 找不到类型（在 build 目录里运行时会从磁盘上的模块目录找到，看不出问题）
qt_standard_project_setup(REQUIRES 6.8)
find_package(Dtk6 REQUIRED COMPONENTS Core)
if(Qt6_VERSION VERSION_GREATER_EQUAL 6.10)
    find_package(Qt6 REQUIRED COMPONENTS WaylandClientPrivate QuickPrivate)
endif()
find_package(TreelandProtocols REQUIRED)
# 可选：Qt WebEngine。有的话壁纸包的 QML 里可以用 WebEngineView 跑网页（Chromium 的 GPU 线程和 Qt Quick 共享 GL 上下文）
find_package(Qt6 QUIET OPTIONAL_COMPONENTS WebEngineQuick)

# libmpv：优先用 pkg-config；没装 libmpv-dev 时用解开的头文件（WWP_MPV_PREFIX）+ 系统里的 libmpv.so.2
set(WWP_MPV_PREFIX "${CMAKE_CURRENT_SOURCE_DIR}/../third_party/mpvdev" CACHE PATH "解开的 libmpv-dev 根目录")
pkg_check_modules(MPV QUIET IMPORTED_TARGET mpv)
if(MPV_FOUND)
    set(WWP_MPV_TARGET PkgConfig::MPV)
else()
    find_path(MPV_INCLUDE_DIR mpv/client.h HINTS "${WWP_MPV_PREFIX}/usr/include" REQUIRED)
    find_library(MPV_LIBRARY NAMES mpv libmpv.so.2 REQUIRED)
    add_library(wwp_mpv INTERFACE)
    target_include_directories(wwp_mpv INTERFACE "${MPV_INCLUDE_DIR}")
    target_link_libraries(wwp_mpv INTERFACE "${MPV_LIBRARY}")
    set(WWP_MPV_TARGET wwp_mpv)
    message(STATUS "libmpv: ${MPV_INCLUDE_DIR} ${MPV_LIBRARY}")
endif()

qt_add_executable(${BIN_NAME}
    main.cpp
)

qt6_generate_wayland_protocol_client_sources(${BIN_NAME}
    FILES
        ${TREELAND_PROTOCOLS_DATA_DIR}/treeland-wallpaper-shell-unstable-v1.xml
)

qt_add_qml_module(${BIN_NAME}
    URI com.treeland.wallfactory
    VERSION 1.0
    SOURCES
    loggings.h
    loggings.cpp
    mpvvideoitem.h
    mpvvideoitem.cpp
    mpvvideocontroller.h
    mpvvideocontroller.cpp
    qwaylandwallpapershellintegration_p.h
    qwaylandwallpapershellintegration.cpp
    qwaylandwallpapershellintegrationplugin.cpp
    qwaylandwallpapersurface_p.h
    qwaylandwallpapersurface.cpp
    wallpaperwindow.h
    wallpaperwindow.cpp
    treelandwallpapernotifierclient.h
    treelandwallpapernotifierclient.cpp
    wwpcontroller.h
    wwpcontroller.cpp
    QML_FILES
    Image.qml
    Video.qml
    WwpScene.qml
)

target_link_libraries(${BIN_NAME}
    PRIVATE
        Qt6::Quick
        Qt6::QuickPrivate
        Qt6::WaylandClient
        Qt6::WaylandClientPrivate
        Dtk6::Core
        ${WWP_MPV_TARGET}
)
if(TARGET Qt6::WebEngineQuick)
    target_link_libraries(${BIN_NAME} PRIVATE Qt6::WebEngineQuick)
    target_compile_definitions(${BIN_NAME} PRIVATE WWP_HAVE_WEBENGINE)
    message(STATUS "Qt WebEngine：启用（壁纸包可用 WebEngineView）")
else()
    message(STATUS "Qt WebEngine：没找到 Qt6WebEngineQuick（qt6-webengine-dev），壁纸包不能用 WebEngineView")
endif()

install(TARGETS ${BIN_NAME} RUNTIME DESTINATION "${CMAKE_INSTALL_BINDIR}")

# wwp-set：命令行切换壁纸（和控制中心走同一个 treeland_wallpaper_manager_v1 协议），测试和脚本用
find_program(WAYLAND_SCANNER wayland-scanner REQUIRED)
pkg_check_modules(WAYLAND_CLIENT REQUIRED IMPORTED_TARGET wayland-client)
set(WWP_MGR_XML ${TREELAND_PROTOCOLS_DATA_DIR}/treeland-wallpaper-manager-unstable-v1.xml)
set(WWP_MGR_H ${CMAKE_CURRENT_BINARY_DIR}/treeland-wallpaper-manager-unstable-v1-client-protocol.h)
set(WWP_MGR_C ${CMAKE_CURRENT_BINARY_DIR}/treeland-wallpaper-manager-unstable-v1-protocol.c)
add_custom_command(OUTPUT ${WWP_MGR_H} ${WWP_MGR_C}
    COMMAND ${WAYLAND_SCANNER} client-header ${WWP_MGR_XML} ${WWP_MGR_H}
    COMMAND ${WAYLAND_SCANNER} private-code ${WWP_MGR_XML} ${WWP_MGR_C}
    DEPENDS ${WWP_MGR_XML})
add_executable(wwp-set ../tools/wwp-set.c ${WWP_MGR_C} ${WWP_MGR_H})
target_include_directories(wwp-set PRIVATE ${CMAKE_CURRENT_BINARY_DIR})
target_link_libraries(wwp-set PRIVATE PkgConfig::WAYLAND_CLIENT)
install(TARGETS wwp-set RUNTIME DESTINATION "${CMAKE_INSTALL_BINDIR}")

# wwp-settings：设置窗口（位置、底层壁纸、模式、卡片），见 ../settings/
add_subdirectory(../settings settings)
