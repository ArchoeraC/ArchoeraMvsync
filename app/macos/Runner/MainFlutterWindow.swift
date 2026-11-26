import Cocoa
import FlutterMacOS
import Metal

class MainFlutterWindow: NSWindow {
  override func awakeFromNib() {
    let flutterViewController = FlutterViewController()
    let windowFrame = self.frame
    self.contentViewController = flutterViewController
    self.setFrame(windowFrame, display: true)

    // GPU 选择探针（只读：不创建 command queue，不持有独立 Metal 上下文）。
    // Flutter 3.47+ 在 macOS 走 Impeller + Metal；「走 Metal」不等于「走独显」：
    // 引擎的 SelectMetalDevice() 默认按 hasUnifiedMemory 挑核显，由
    // GPUSelection.m 的 dyld interpose 改写成高性能设备。这里记录改完之后
    // 引擎实际能拿到的设备，便于真机验收。
    logGPUSelection(reason: "启动")

    // 插拔外接屏、或系统在自动切换显卡时换了驱动屏幕的 GPU，都会回到这里；
    // 重新记录一次，便于真机确认「播放过程中是否被系统切回集显」。
    let center = NotificationCenter.default
    center.addObserver(
      self,
      selector: #selector(gpuSelectionMayHaveChanged(_:)),
      name: NSApplication.didChangeScreenParametersNotification,
      object: nil)
    center.addObserver(
      self,
      selector: #selector(gpuSelectionMayHaveChanged(_:)),
      name: NSWindow.didChangeScreenNotification,
      object: self)

    RegisterGeneratedPlugins(registry: flutterViewController)

    super.awakeFromNib()
  }

  @objc private func gpuSelectionMayHaveChanged(_ notification: Notification) {
    logGPUSelection(reason: notification.name.rawValue)
  }

  /// 记录当前默认 Metal 设备与系统里全部设备。
  ///
  /// 设置页「独立显卡加速」三档里选 `discrete`（或 `auto` 判定独显不弱于集显）时，
  /// `GPUSelection.m` 的 interpose 会把引擎的 `MTLDevice` 换成高性能独显；
  /// 选了 `discrete` 却仍拿到 `lowPower=yes`，说明 interpose 没生效
  /// （自签名 / 加固运行时差异），需要另找方案。
  private func logGPUSelection(reason: String) {
    let allDevices = MTLCopyAllDevices()
    let summary = allDevices.map { dev -> String in
      let kind = dev.isRemovable ? "eGPU" : (dev.isLowPower ? "integrated" : "discrete")
      return "\(dev.name)[\(kind)]"
    }.joined(separator: ", ")
    let discreteCount = allDevices.filter { !$0.isLowPower && !$0.isRemovable }.count
    let gpuMode = ArchoeraMacOSGPUModeDescription()

    guard let device = MTLCreateSystemDefaultDevice() else {
      NSLog(
        "[archoera] GPU[\(reason)]: no Metal device; Impeller cannot use the GPU on this Mac; "
          + "devices(\(allDevices.count))=\(summary)")
      return
    }

    let workingSetMB = Double(device.recommendedMaxWorkingSetSize) / 1024.0 / 1024.0
    NSLog(
      "[archoera] GPU[\(reason)]: preference=\(gpuMode), "
        + "default=\(device.name) (lowPower=\(device.isLowPower ? "yes" : "no"), "
        + "removable=\(device.isRemovable ? "yes" : "no"), "
        + "unified=\(device.hasUnifiedMemory ? "yes" : "no"), "
        + "recommendedWorkingSet=\(Int(workingSetMB))MB); "
        + "devices(\(allDevices.count))=\(summary)")

    if gpuMode == "discrete" && device.isLowPower && discreteCount > 0 {
      NSLog(
        "[archoera] GPU[\(reason)]: WARNING 已选「优先独立显卡」，但引擎仍拿到集显"
          + "（系统里有 \(discreteCount) 个独显）——interpose 未生效，请检查构建 / 签名方式。")
    }
  }
}
