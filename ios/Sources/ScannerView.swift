import AVFoundation
import SwiftUI

/// Native iOS camera QR scanner using AVFoundation (zero third-party dependencies).
struct ScannerView: UIViewControllerRepresentable {
    var onCode: (String) -> Void
    var onCancel: () -> Void

    func makeUIViewController(context: Context) -> ScannerController {
        let controller = ScannerController()
        controller.onCode = onCode
        controller.onCancel = onCancel
        return controller
    }

    func updateUIViewController(_ uiViewController: ScannerController, context: Context) {}
}

final class ScannerController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    var onCode: ((String) -> Void)?
    var onCancel: (() -> Void)?

    private var session: AVCaptureSession?
    private var previewLayer: AVCaptureVideoPreviewLayer?
    private var handled = false

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        setupOverlay()
        checkAndStartCamera()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        previewLayer?.frame = view.bounds
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        stopSession()
    }

    private func setupOverlay() {
        let close = UIButton(type: .system)
        close.setTitle("取消", for: .normal)
        close.setTitleColor(.white, for: .normal)
        close.titleLabel?.font = .systemFont(ofSize: 16, weight: .semibold)
        close.backgroundColor = UIColor.black.withAlphaComponent(0.55)
        close.layer.cornerRadius = 18
        close.contentEdgeInsets = UIEdgeInsets(top: 8, left: 16, bottom: 8, right: 16)
        close.translatesAutoresizingMaskIntoConstraints = false
        close.addTarget(self, action: #selector(cancelTapped), for: .touchUpInside)
        view.addSubview(close)

        let hint = UILabel()
        hint.text = "对准电脑端 Remote AI 的连接二维码"
        hint.textColor = .white
        hint.font = .systemFont(ofSize: 14, weight: .medium)
        hint.textAlignment = .center
        hint.backgroundColor = UIColor.black.withAlphaComponent(0.5)
        hint.layer.cornerRadius = 12
        hint.clipsToBounds = true
        hint.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(hint)

        NSLayoutConstraint.activate([
            close.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 16),
            close.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            hint.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -36),
            hint.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            hint.widthAnchor.constraint(greaterThanOrEqualToConstant: 260),
            hint.heightAnchor.constraint(equalToConstant: 36),
        ])
    }

    @objc private func cancelTapped() {
        stopSession()
        onCancel?()
    }

    private func checkAndStartCamera() {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            startSession()
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
                DispatchQueue.main.async {
                    if granted { self?.startSession() }
                    else { self?.showUnavailable("请在“设置 -> Remote AI”中允许相机权限，或返回手动输入地址。") }
                }
            }
        default:
            showUnavailable("相机权限未开启，可返回使用手动输入地址与密码连接。")
        }
    }

    private func startSession() {
        let capture = AVCaptureSession()
        guard let device = AVCaptureDevice.default(for: .video),
              let input = try? AVCaptureDeviceInput(device: device),
              capture.canAddInput(input) else {
            showUnavailable("当前设备没有可用摄像头，请返回手动输入地址连接。")
            return
        }
        capture.addInput(input)
        let output = AVCaptureMetadataOutput()
        guard capture.canAddOutput(output) else { return }
        capture.addOutput(output)
        output.setMetadataObjectsDelegate(self, queue: .main)
        output.metadataObjectTypes = [.qr]

        let preview = AVCaptureVideoPreviewLayer(session: capture)
        preview.videoGravity = .resizeAspectFill
        preview.frame = view.bounds
        view.layer.insertSublayer(preview, at: 0)
        previewLayer = preview
        session = capture
        DispatchQueue.global(qos: .userInitiated).async {
            capture.startRunning()
        }
    }

    private func stopSession() {
        if let capture = session, capture.isRunning {
            DispatchQueue.global(qos: .userInitiated).async {
                capture.stopRunning()
            }
        }
    }

    private func showUnavailable(_ text: String) {
        let label = UILabel()
        label.text = text
        label.textColor = .white
        label.numberOfLines = 0
        label.textAlignment = .center
        label.font = .systemFont(ofSize: 15)
        label.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(label)
        NSLayoutConstraint.activate([
            label.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            label.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 28),
            label.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -28),
        ])
    }

    func metadataOutput(
        _ output: AVCaptureMetadataOutput,
        didOutput metadataObjects: [AVMetadataObject],
        from connection: AVCaptureConnection
    ) {
        guard !handled,
              let obj = metadataObjects.first as? AVMetadataMachineReadableCodeObject,
              let value = obj.stringValue, !value.isEmpty else {
            return
        }
        handled = true
        UIImpactFeedbackGenerator(style: .medium).impactOccurred()
        stopSession()
        onCode?(value)
    }
}
