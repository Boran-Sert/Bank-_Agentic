package com.turkiyefinans.actions.atmbranch.assistant.aitext.adc;

import com.fasterxml.jackson.databind.ObjectMapper;
import com.turkiyefinans.core.utils.LogUtil;
import org.springframework.stereotype.Service;

import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

@Service
public class AdcAIClient {

    // .NET AI orchestrator endpoint
    private static final String ENDPOINT = "http://10.141.5.253:5080/api/ai/chat";

    private final ObjectMapper objectMapper = new ObjectMapper();

    public AdcAIResponse send(AdcAIRequest request) throws Exception {
        String requestBody = objectMapper.writeValueAsString(request);
        LogUtil.getLog().info("AdcAIClient request: " + requestBody);

        HttpURLConnection connection = null;
        try {
            connection = (HttpURLConnection) new URL(ENDPOINT).openConnection();
            connection.setRequestMethod("POST");
            connection.setRequestProperty("Content-Type", "application/json; charset=UTF-8");
            connection.setRequestProperty("Accept", "application/json");
            connection.setConnectTimeout(5000);
            connection.setReadTimeout(30000);
            connection.setDoOutput(true);

            try (OutputStream os = connection.getOutputStream()) {
                os.write(requestBody.getBytes(StandardCharsets.UTF_8));
            }

            int code = connection.getResponseCode();
            if (code == HttpURLConnection.HTTP_OK) {
                byte[] responseBytes = connection.getInputStream().readAllBytes();
                LogUtil.getLog().info("AdcAIClient response: " + new String(responseBytes, StandardCharsets.UTF_8));
                return objectMapper.readValue(responseBytes, AdcAIResponse.class);
            }
            String error = new String(connection.getErrorStream().readAllBytes(), StandardCharsets.UTF_8);
            throw new RuntimeException("ADC AI error " + code + ": " + error);
        } finally {
            if (connection != null) connection.disconnect();
        }
    }
}
