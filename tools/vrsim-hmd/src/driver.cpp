// 仮想の HMD（SteamVR のドライバ）。頭の位置と向きを UDP で受け取り、そのまま SteamVR に渡す。
// VRChat を VR モードで動かし、頭と手（手は Virtual Motion Tracker）を scripts/vrsim.py の台本どおりに動かして確かめるためのもの。
// OpenVR のサンプル（samples/drivers/drivers/simplehmd、BSD-3-Clause）を元に、1ファイルにまとめた。
//
// 受け取る形: 127.0.0.1:<port>（既定 39580）に、little-endian の float 7つ（x y z qx qy qz qw）。OpenVR の右手系（+Y が上、-Z が前、メートル）。
// 画面: デスクトップの窓（window_* の大きさ）に左右の目を並べて出す。

#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>

#include <atomic>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

#include "openvr_driver.h"

#pragma comment(lib, "ws2_32.lib")

namespace
{
const char *const kSection = "driver_mmdhmd";

void Log(const char *message)
{
	if (vr::VRDriverLog())
		vr::VRDriverLog()->Log(message);
}

class Display : public vr::IVRDisplayComponent
{
public:
	int32_t x = 0, y = 0, width = 1280, height = 720, renderWidth = 1280, renderHeight = 1440;

	bool IsDisplayOnDesktop() override { return true; }
	bool IsDisplayRealDisplay() override { return false; }
	void GetRecommendedRenderTargetSize(uint32_t *w, uint32_t *h) override
	{
		*w = renderWidth;
		*h = renderHeight;
	}
	void GetEyeOutputViewport(vr::EVREye eye, uint32_t *px, uint32_t *py, uint32_t *w, uint32_t *h) override
	{
		*py = 0;
		*w = width / 2;
		*h = height;
		*px = eye == vr::Eye_Left ? 0 : width / 2;
	}
	void GetProjectionRaw(vr::EVREye, float *left, float *right, float *top, float *bottom) override
	{
		// 左右・上下とも 90°
		*left = -1.0f;
		*right = 1.0f;
		*top = -1.0f;
		*bottom = 1.0f;
	}
	vr::DistortionCoordinates_t ComputeDistortion(vr::EVREye, float u, float v) override
	{
		vr::DistortionCoordinates_t c{};
		c.rfRed[0] = c.rfGreen[0] = c.rfBlue[0] = u;
		c.rfRed[1] = c.rfGreen[1] = c.rfBlue[1] = v;
		return c;
	}
	void GetWindowBounds(int32_t *px, int32_t *py, uint32_t *w, uint32_t *h) override
	{
		*px = x;
		*py = y;
		*w = width;
		*h = height;
	}
	bool ComputeInverseDistortion(vr::HmdVector2_t *, vr::EVREye, uint32_t, float, float) override { return false; }
};

class Hmd : public vr::ITrackedDeviceServerDriver
{
public:
	std::string serial = "MMDHMD-0";
	std::string model = "MMD World virtual HMD";
	int port = 39580;
	Display display;

	vr::EVRInitError Activate(uint32_t index) override
	{
		index_ = index;
		auto c = vr::VRProperties()->TrackedDeviceToPropertyContainer(index);
		vr::VRProperties()->SetStringProperty(c, vr::Prop_ModelNumber_String, model.c_str());
		vr::VRProperties()->SetStringProperty(c, vr::Prop_ManufacturerName_String, "MMD World");
		vr::VRProperties()->SetFloatProperty(c, vr::Prop_UserIpdMeters_Float, 0.063f);
		vr::VRProperties()->SetFloatProperty(c, vr::Prop_DisplayFrequency_Float, 60.0f);
		vr::VRProperties()->SetFloatProperty(c, vr::Prop_UserHeadToEyeDepthMeters_Float, 0.0f);
		vr::VRProperties()->SetFloatProperty(c, vr::Prop_SecondsFromVsyncToPhotons_Float, 0.011f);
		vr::VRProperties()->SetBoolProperty(c, vr::Prop_IsOnDesktop_Bool, false);
		vr::VRProperties()->SetBoolProperty(c, vr::Prop_DisplayDebugMode_Bool, true);
		vr::VRProperties()->SetStringProperty(c, vr::Prop_InputProfilePath_String, "{mmdhmd}/input/mmdhmd_profile.json");

		active_ = true;
		receiver_ = std::thread(&Hmd::Receive, this);
		updater_ = std::thread(&Hmd::Update, this);
		return vr::VRInitError_None;
	}

	void Deactivate() override
	{
		if (active_.exchange(false))
		{
			closesocket(socket_);
			receiver_.join();
			updater_.join();
		}
		index_ = vr::k_unTrackedDeviceIndexInvalid;
	}

	void EnterStandby() override {}
	void *GetComponent(const char *name) override
	{
		return std::strcmp(name, vr::IVRDisplayComponent_Version) == 0 ? &display : nullptr;
	}
	void DebugRequest(const char *, char *response, uint32_t size) override
	{
		if (size > 0)
			response[0] = 0;
	}

	vr::DriverPose_t GetPose() override
	{
		vr::DriverPose_t pose{};
		pose.qWorldFromDriverRotation.w = 1.0;
		pose.qDriverFromHeadRotation.w = 1.0;
		{
			std::lock_guard<std::mutex> lock(mutex_);
			pose.vecPosition[0] = pose_[0];
			pose.vecPosition[1] = pose_[1];
			pose.vecPosition[2] = pose_[2];
			pose.qRotation.x = pose_[3];
			pose.qRotation.y = pose_[4];
			pose.qRotation.z = pose_[5];
			pose.qRotation.w = pose_[6];
		}
		pose.poseIsValid = true;
		pose.deviceIsConnected = true;
		pose.result = vr::TrackingResult_Running_OK;
		return pose;
	}

private:
	std::atomic<bool> active_{false};
	std::atomic<uint32_t> index_{vr::k_unTrackedDeviceIndexInvalid};
	std::thread receiver_, updater_;
	SOCKET socket_ = INVALID_SOCKET;
	std::mutex mutex_;
	// 届くまでは、原点の 1.6m 上で前を向いて立っていることにする
	float pose_[7] = {0.0f, 1.6f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f};

	void Update()
	{
		while (active_)
		{
			vr::VRServerDriverHost()->TrackedDevicePoseUpdated(index_, GetPose(), sizeof(vr::DriverPose_t));
			std::this_thread::sleep_for(std::chrono::milliseconds(8));
		}
	}

	void Receive()
	{
		WSADATA wsa;
		WSAStartup(MAKEWORD(2, 2), &wsa);
		socket_ = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
		sockaddr_in addr{};
		addr.sin_family = AF_INET;
		addr.sin_port = htons(static_cast<u_short>(port));
		addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
		if (bind(socket_, reinterpret_cast<sockaddr *>(&addr), sizeof(addr)) != 0)
		{
			Log("mmdhmd: UDP のポートを開けない");
			return;
		}
		Log(("mmdhmd: UDP 127.0.0.1:" + std::to_string(port) + " で頭の姿勢を待つ").c_str());
		float buffer[16];
		while (active_)
		{
			int n = recv(socket_, reinterpret_cast<char *>(buffer), sizeof(buffer), 0);
			if (n == static_cast<int>(sizeof(float) * 7))
			{
				std::lock_guard<std::mutex> lock(mutex_);
				std::memcpy(pose_, buffer, sizeof(pose_));
			}
			else if (n < 0)
			{
				break;
			}
		}
		WSACleanup();
	}
};

class Provider : public vr::IServerTrackedDeviceProvider
{
public:
	vr::EVRInitError Init(vr::IVRDriverContext *context) override
	{
		VR_INIT_SERVER_DRIVER_CONTEXT(context);
		hmd_ = std::make_unique<Hmd>();
		auto settings = vr::VRSettings();
		char text[256];
		vr::EVRSettingsError error;
		settings->GetString(kSection, "serial_number", text, sizeof(text), &error);
		if (error == vr::VRSettingsError_None && text[0])
			hmd_->serial = text;
		int port = settings->GetInt32(kSection, "port", &error);
		if (error == vr::VRSettingsError_None && port > 0)
			hmd_->port = port;
		auto &d = hmd_->display;
		d.x = settings->GetInt32(kSection, "window_x");
		d.y = settings->GetInt32(kSection, "window_y");
		d.width = settings->GetInt32(kSection, "window_width");
		d.height = settings->GetInt32(kSection, "window_height");
		d.renderWidth = settings->GetInt32(kSection, "render_width");
		d.renderHeight = settings->GetInt32(kSection, "render_height");
		if (!vr::VRServerDriverHost()->TrackedDeviceAdded(hmd_->serial.c_str(), vr::TrackedDeviceClass_HMD, hmd_.get()))
		{
			Log("mmdhmd: HMD を足せなかった");
			return vr::VRInitError_Driver_Unknown;
		}
		return vr::VRInitError_None;
	}
	void Cleanup() override { hmd_ = nullptr; }
	const char *const *GetInterfaceVersions() override { return vr::k_InterfaceVersions; }
	void RunFrame() override
	{
		vr::VREvent_t e{};
		while (vr::VRServerDriverHost()->PollNextEvent(&e, sizeof(e)))
		{
		}
	}
	bool ShouldBlockStandbyMode() override { return false; }
	void EnterStandby() override {}
	void LeaveStandby() override {}

private:
	std::unique_ptr<Hmd> hmd_;
};

Provider provider;
} // namespace

extern "C" __declspec(dllexport) void *HmdDriverFactory(const char *name, int *code)
{
	if (std::strcmp(vr::IServerTrackedDeviceProvider_Version, name) == 0)
		return &provider;
	if (code)
		*code = vr::VRInitError_Init_InterfaceNotFound;
	return nullptr;
}
